using System.Diagnostics;
using System.Reflection;

namespace SebastianCi.Web;

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

    public RunManager(string repositoryPath) => _repositoryPath = repositoryPath;

    /// <summary>実行中かどうか。</summary>
    public bool IsRunning
    {
        get { lock (_syncRoot) { return _currentProcess is { HasExited: false }; } }
    }

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
        IReadOnlyDictionary<string, string>? parameters = null, bool autoApprove = false)
    {
        lock (_syncRoot)
        {
            if (_currentProcess is { HasExited: false }) return false;

            _outputLines.Clear();
            _lastExitCode = null;
            _currentProcess = StartProcess(rebuild, jobId, parameters, autoApprove);
        }

        return true;
    }

    private Process StartProcess(
        bool rebuild, string? jobId, IReadOnlyDictionary<string, string>? parameters, bool autoApprove)
    {
        Process process = new() { StartInfo = BuildStartInfo(rebuild, jobId, parameters, autoApprove) };
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
        bool rebuild, string? jobId, IReadOnlyDictionary<string, string>? parameters, bool autoApprove)
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
        foreach (string argument in BuildArguments(dllPath, rebuild, jobId, parameters, autoApprove))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private IEnumerable<string> BuildArguments(
        string dllPath, bool rebuild, string? jobId,
        IReadOnlyDictionary<string, string>? parameters, bool autoApprove)
    {
        yield return dllPath;
        yield return _repositoryPath;
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
        lock (_syncRoot)
        {
            _lastExitCode = process.ExitCode;
        }
    }
}
