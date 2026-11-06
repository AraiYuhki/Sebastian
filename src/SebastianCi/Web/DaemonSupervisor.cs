using System.Diagnostics;

namespace SebastianCi.Web;

/// <summary>
/// `serve` を子プロセスとして起動し、異常終了したら指数バックオフで自動再起動する常駐スーパーバイザー。
/// 状態は daemon.json、出力は daemon.log（分離起動時）に残す。
/// </summary>
public sealed class DaemonSupervisor
{
    /// <summary>分離起動された（コンソールに書けない）ことを示す環境変数。</summary>
    public const string DetachedEnvironmentVariable = "SEBASTIAN_CI_DAEMON_DETACHED";

    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan HealthyUptime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly ServeOptions _options;
    private readonly string _dataRootPath;
    private readonly object _logLock = new();
    private StreamWriter? _logWriter;
    private DaemonState _state = null!;

    public DaemonSupervisor(ServeOptions options)
    {
        _options = options;
        _dataRootPath = options.ResolveDataRootPath();
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRootPath);
        using Process self = Process.GetCurrentProcess();
        if (Environment.GetEnvironmentVariable(DetachedEnvironmentVariable) == "1") OpenLog();

        DaemonState? existing = DaemonState.Load(_dataRootPath);
        if (existing is not null && existing.SupervisorPid != self.Id && existing.IsSupervisorAlive())
        {
            Log($"既に別のスーパーバイザー(PID {existing.SupervisorPid})が稼働中のため終了します。");
            return 1;
        }

        _state = new DaemonState(
            self.Id, self.StartTime.ToUniversalTime(), _options.Port,
            _options.Hub ? $"(ハブ) {_dataRootPath}" : Path.GetFullPath(_options.RepositoryPath),
            null, 0, null, null);
        _state.Save(_dataRootPath);
        Log($"スーパーバイザーを開始しました (PID {self.Id}, ポート {_options.Port})。");

        try
        {
            await SuperviseAsync(cancellationToken);
        }
        finally
        {
            // 自分の状態ファイルだけを片付ける（後から起動した別の常駐のものは消さない）。
            if (DaemonState.Load(_dataRootPath)?.SupervisorPid == self.Id) DaemonState.Delete(_dataRootPath);
            Log("スーパーバイザーを停止しました。");
            _logWriter?.Dispose();
        }

        return 0;
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            int exitCode = await RunServerOnceAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (exitCode == 0)
            {
                Log("serve が正常終了したため、スーパーバイザーも終了します。");
                return;
            }

            consecutiveFailures = DateTimeOffset.UtcNow - startedAt >= HealthyUptime ? 1 : consecutiveFailures + 1;
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(
                Math.Pow(2, Math.Min(consecutiveFailures - 1, 6)), MaxBackoff.TotalSeconds));
            _state = _state with
            {
                ServerPid = null, RestartCount = _state.RestartCount + 1,
                LastExitCode = exitCode, LastRestartAt = DateTimeOffset.UtcNow
            };
            _state.Save(_dataRootPath);
            Log($"serve が異常終了しました (終了コード {exitCode})。{delay.TotalSeconds:0} 秒後に再起動します。");

            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<int> RunServerOnceAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = SelfLauncher.CreateStartInfo(["serve", .. _options.ToServeArguments()]);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        if (_options.Token is not null)
        {
            startInfo.Environment[ServeOptions.TokenEnvironmentVariableName] = _options.Token;
        }

        using Process server = new() { StartInfo = startInfo };
        server.OutputDataReceived += (_, e) => LogChildLine(e.Data);
        server.ErrorDataReceived += (_, e) => LogChildLine(e.Data);
        server.Start();
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        _state = _state with { ServerPid = server.Id };
        _state.Save(_dataRootPath);
        Log($"serve を起動しました (PID {server.Id})。");

        try
        {
            await server.WaitForExitAsync(cancellationToken);
            server.WaitForExit(); // 非同期の出力読み取りが完了するまで待つ
            return server.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKill(server);
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception) { }
    }

    private void OpenLog()
    {
        string logPath = DaemonState.GetLogPath(_dataRootPath);
        if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxLogBytes)
        {
            File.Move(logPath, logPath + ".1", overwrite: true);
        }

        _logWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    private void LogChildLine(string? line)
    {
        if (line is not null) Write(line);
    }

    private void Log(string message) => Write($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [daemon] {message}");

    private void Write(string line)
    {
        lock (_logLock)
        {
            if (_logWriter is not null) _logWriter.WriteLine(line);
            else Console.WriteLine(line);
        }
    }
}
