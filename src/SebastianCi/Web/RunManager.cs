using System.Diagnostics;
using System.Reflection;

using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>1回の実行の結末。Skipped は「実行済みコミットのためジョブを1つも実行しなかった」場合。ChainPath は連鎖で起動された場合の起点からの経路。</summary>
public sealed record RunCompletion(int ExitCode, bool Skipped, IReadOnlyList<string> ChainPath)
{
    public bool IsSuccess => ExitCode == 0;
}

/// <summary>
/// ダッシュボードからの「実行」を、sebastian-ci 本体を子プロセスとして起動する形で管理する。
/// これにより git・コンテナ・通知・履歴などの既存ロジックをそのまま再利用できる。
/// 同時に走らせるのは1件までとし、出力行をスレッドセーフに蓄積する。
/// </summary>
public sealed class RunManager
{
    private readonly string _repositoryPath;
    private readonly object _syncRoot = new();
    private readonly List<string> _outputLines = new();

    private Process? _currentProcess;
    private int? _lastExitCode;
    private bool _preparing;
    private bool _exitHandled = true;
    private static readonly TimeSpan OutputFlushTimeout = TimeSpan.FromSeconds(5);
    private IReadOnlyList<string> _chainPath = [];
    private readonly PrepareRun? _prepare;
    private readonly CancellationToken _stopping;

    private readonly string? _configFileName;
    private readonly string? _dataRootPath;

    /// <summary>
    /// 実行の直前に行う準備（ワークスペース型ジョブのソース取得など）。進捗は log に1行ずつ流す。
    /// 戻り値は実行に使うリビジョン（--revision）で、null なら準備に失敗したとして実行しない。
    /// </summary>
    public delegate Task<string?> PrepareRun(Action<string> log, CancellationToken cancellationToken);

    /// <param name="prepare">省略すると準備なし（Git リポジトリを直接実行する）。</param>
    /// <param name="stopping">アプリケーション停止の通知。準備中のコマンドを止めるのに使う。</param>
    /// <param name="configFileName">実行に使う設定ファイル名。省略すると本体の既定（.sebastian-ci.yaml）。</param>
    /// <param name="dataRootPath">履歴・ログの保存先。省略すると本体の既定（リポジトリ内の .sebastian-ci）。</param>
    public RunManager(
        string repositoryPath, string? configFileName = null, string? dataRootPath = null,
        PrepareRun? prepare = null, CancellationToken stopping = default)
    {
        _prepare = prepare;
        _stopping = stopping;
        _repositoryPath = repositoryPath;
        _configFileName = configFileName;
        _dataRootPath = dataRootPath;
    }

    public bool HasPreparation => _prepare is not null;

    /// <summary>実行が終わるたびに（成否を問わず）呼ばれる。ロックの外・スレッドプール上で呼ぶので、中で別の実行を始めてもよい。</summary>
    public event Action<RunCompletion>? Completed;

    /// <summary>実行中かどうか。</summary>
    public bool IsRunning
    {
        get { lock (_syncRoot) { return IsBusy; } }
    }

    /// <summary>準備中、または本体が動いている（終了後の出力・終了コードの確定待ちを含む）。呼び出し側でロックすること。</summary>
    private bool IsBusy => _preparing || (_currentProcess is not null && !_exitHandled);

    /// <summary>現在の状態（実行中フラグ・直近の終了コード・蓄積された出力）のスナップショットを返す。</summary>
    public RunStatus GetStatus()
    {
        lock (_syncRoot)
        {
            return new RunStatus(IsRunning, _lastExitCode, _outputLines.ToList());
        }
    }

    /// <summary>
    /// 新しい実行を開始する。jobId を指定するとそのジョブ（と依存）だけを実行する。
    /// parameters は --param として、autoApprove は --yes として本体へ渡される。
    /// 既に実行中なら false を返す。
    /// </summary>
    public bool TryStart(
        bool rebuild, string? jobId = null,
        IReadOnlyDictionary<string, string>? parameters = null, bool autoApprove = false,
        IReadOnlyList<string>? chainPath = null, string? startNote = null)
    {
        lock (_syncRoot)
        {
            if (IsBusy) return false;

            _outputLines.Clear();
            _lastExitCode = null;
            _chainPath = chainPath ?? [];
            if (startNote is not null) _outputLines.Add(startNote);
            if (_prepare is not null)
            {
                _preparing = true;
                _ = Task.Run(() => PrepareAndStartAsync(rebuild, jobId, parameters, autoApprove));
                return true;
            }

            _currentProcess = StartProcess(rebuild, jobId, parameters, autoApprove, null);
            _exitHandled = false;
        }

        return true;
    }

    /// <summary>準備（ソース取得など）を済ませてから本体を起動する。準備中も「実行中」として扱い、二重起動を防ぐ。</summary>
    private async Task PrepareAndStartAsync(
        bool rebuild, string? jobId, IReadOnlyDictionary<string, string>? parameters, bool autoApprove)
    {
        string? revision = null;
        try
        {
            revision = await _prepare!(AppendLine, _stopping);
        }
        catch (Exception exception)
        {
            AppendLine($"💥 実行の準備に失敗しました: {exception.Message}");
        }

        RunCompletion? failed = null;
        lock (_syncRoot)
        {
            try
            {
                if (revision is null)
                {
                    _lastExitCode = 1;
                    failed = new RunCompletion(1, false, _chainPath);
                }
                else
                {
                    _currentProcess = StartProcess(rebuild, jobId, parameters, autoApprove, revision);
                    _exitHandled = false;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppendLine($"💥 実行を開始できません: {exception.Message}");
                _lastExitCode = 1;
                failed = new RunCompletion(1, false, _chainPath);
            }
            finally
            {
                _preparing = false; // プロセス登録と同じロック内で戻し、「実行中でない」瞬間を作らない
            }
        }

        if (failed is not null) RaiseCompleted(failed);
    }

    private void RaiseCompleted(RunCompletion completion)
    {
        try
        {
            Completed?.Invoke(completion);
        }
        catch (Exception exception)
        {
            // 通知先（連鎖の起動など）の失敗で、実行の後始末を止めない。
            ConsoleLogger.WriteWarning($"⚠ 実行完了の後処理でエラーが発生しました: {exception.Message}");
        }
    }

    private Process StartProcess(
        bool rebuild, string? jobId, IReadOnlyDictionary<string, string>? parameters, bool autoApprove, string? revision)
    {
        Process process = new() { StartInfo = BuildStartInfo(rebuild, jobId, parameters, autoApprove, revision) };
        process.OutputDataReceived += (_, e) => AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(e.Data);
        process.Exited += (_, _) => OnExited(process);
        process.EnableRaisingEvents = true;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private ProcessStartInfo BuildStartInfo(
        bool rebuild, string? jobId, IReadOnlyDictionary<string, string>? parameters, bool autoApprove, string? revision)
    {
        string dllPath = Assembly.GetEntryAssembly()!.Location;
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in BuildArguments(dllPath, rebuild, jobId, parameters, autoApprove, revision))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>本体へ渡す引数（先頭は本体の dll）。ダッシュボードで選んだ設定ファイルと保存先を実行にも使う。</summary>
    public IEnumerable<string> BuildArguments(
        string dllPath, bool rebuild, string? jobId,
        IReadOnlyDictionary<string, string>? parameters, bool autoApprove, string? revision = null)
    {
        yield return dllPath;
        yield return _repositoryPath;
        if (!string.IsNullOrWhiteSpace(_configFileName))
        {
            yield return "--config";
            yield return _configFileName;
        }

        if (!string.IsNullOrWhiteSpace(_dataRootPath))
        {
            yield return "--data-dir";
            yield return _dataRootPath;
        }

        if (revision is not null)
        {
            yield return "--revision";
            yield return revision;
        }

        if (rebuild) yield return "--rebuild";
        if (autoApprove) yield return "--yes";
        foreach ((string name, string value) in parameters ?? new Dictionary<string, string>())
        {
            yield return "--param";
            yield return $"{name}={value}";
        }

        if (string.IsNullOrWhiteSpace(jobId)) yield break;

        yield return "--job";
        yield return jobId;
    }

    private void AppendLine(string? line)
    {
        if (line is null) return;

        lock (_syncRoot)
        {
            _outputLines.Add(line);
        }
    }

    private void OnExited(Process process)
    {
        // 非同期の出力読み取りを完了させ、末尾の出力行（スキップの目印など）を取りこぼさない。
        // ジョブが起動した常駐プロセスがパイプを握り続けても実行中のまま固まらないよう、待つのは数秒までにする。
        try { Task.Run(process.WaitForExit).Wait(OutputFlushTimeout); }
        catch (AggregateException) { }

        RunCompletion completion;
        lock (_syncRoot)
        {
            try
            {
                _lastExitCode = process.ExitCode;
                bool skipped = process.ExitCode == 0 && _outputLines.Any(line => line.Contains(RunMarkers.AlreadyBuilt));
                completion = new RunCompletion(process.ExitCode, skipped, _chainPath);
            }
            finally
            {
                _exitHandled = true; // 出力と終了コードがそろってから「実行中ではない」に切り替える
            }
        }

        RaiseCompleted(completion);
    }
}
