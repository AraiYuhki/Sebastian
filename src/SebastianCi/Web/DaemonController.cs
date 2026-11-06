using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>`daemon start / stop / restart / status` の実装。常駐スーパーバイザーを分離起動・停止・照会する。</summary>
public static class DaemonController
{
    private const int NotRunningExitCode = 3;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> StartAsync(ServeOptions options)
    {
        string dataRoot = options.ResolveDataRootPath();
        DaemonState? current = DaemonState.Load(dataRoot);
        if (current is not null && current.IsSupervisorAlive())
        {
            ConsoleLogger.WriteWarning($"⚠ 既に常駐中です (PID {current.SupervisorPid}, ポート {current.Port})。");
            return 0;
        }

        if (!IsPortFree(options.Port))
        {
            ConsoleLogger.WriteError($"💥 ポート {options.Port} は既に使われています。--port で別の番号を指定してください。");
            return 1;
        }

        if (options.Token is null)
        {
            ConsoleLogger.WriteWarning("⚠ トークン認証なしで常駐させます。localhost 以外に公開する場合は --token を指定してください。");
        }

        Directory.CreateDirectory(dataRoot);
        DaemonState.Delete(dataRoot); // 古い（死んだ）状態を読まないよう先に消す
        using Process supervisor = LaunchSupervisor(options);
        return await WaitUntilReadyAsync(supervisor, options, dataRoot) ? 0 : 1;
    }

    public static int Stop(ServeOptions options)
    {
        string dataRoot = options.ResolveDataRootPath();
        DaemonState? state = DaemonState.Load(dataRoot);
        if (state is null || !state.IsSupervisorAlive())
        {
            DaemonState.Delete(dataRoot);
            ConsoleLogger.WriteInfo("常駐していません。");
            return 0;
        }

        try
        {
            using Process supervisor = Process.GetProcessById(state.SupervisorPid);
            supervisor.Kill(entireProcessTree: true);
            if (!supervisor.WaitForExit(StopTimeout))
            {
                ConsoleLogger.WriteError($"💥 PID {state.SupervisorPid} が停止しませんでした。");
                return 1;
            }
        }
        catch (ArgumentException)
        {
            // 判定から kill までの間に自然終了した。目的は達成済み。
        }

        DaemonState.Delete(dataRoot);
        ConsoleLogger.WriteSuccess("🛑 常駐を停止しました。");
        return 0;
    }

    public static async Task<int> RestartAsync(ServeOptions options)
    {
        int stopResult = Stop(options);
        return stopResult != 0 ? stopResult : await StartAsync(options);
    }

    public static int Status(ServeOptions options)
    {
        string dataRoot = options.ResolveDataRootPath();
        DaemonState? state = DaemonState.Load(dataRoot);
        if (state is null || !state.IsSupervisorAlive())
        {
            ConsoleLogger.WriteWarning("● 停止中（常駐していません）");
            return NotRunningExitCode;
        }

        TimeSpan uptime = DateTimeOffset.UtcNow - state.SupervisorStartedAt;
        ConsoleLogger.WriteSuccess($"● 常駐中: http://localhost:{state.Port}");
        Console.WriteLine($"  リポジトリ      : {state.RepositoryPath}");
        Console.WriteLine($"  スーパーバイザー: PID {state.SupervisorPid}（稼働 {FormatUptime(uptime)}）");
        Console.WriteLine($"  serve           : {(state.ServerPid is { } pid ? $"PID {pid}" : "再起動待ち")}");
        Console.WriteLine($"  自動再起動      : {state.RestartCount} 回"
            + (state.LastExitCode is { } code ? $"（直近の終了コード {code}）" : ""));
        Console.WriteLine($"  ログ            : {DaemonState.GetLogPath(dataRoot)}");
        return 0;
    }

    private static Process LaunchSupervisor(ServeOptions options)
    {
        ProcessStartInfo startInfo = SelfLauncher.CreateStartInfo(["daemon", "run", .. options.ToServeArguments()]);
        // 親のコンソールと切り離す。以降の出力はスーパーバイザー側が daemon.log に書く。
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment[DaemonSupervisor.DetachedEnvironmentVariable] = "1";
        if (options.Token is not null)
        {
            startInfo.Environment[ServeOptions.TokenEnvironmentVariableName] = options.Token;
        }

        Process process;
        // 呼び出し元のパイプ（`daemon start | …` など）を常駐プロセスが握り続けないよう、起動の間だけ継承を止める。
        using (StdHandleGuard.SuppressInheritance())
        {
            process = Process.Start(startInfo)!;
        }

        process.StandardInput.Close();
        return process;
    }

    private static async Task<bool> WaitUntilReadyAsync(Process supervisor, ServeOptions options, string dataRoot)
    {
        DateTime deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (supervisor.HasExited)
            {
                ConsoleLogger.WriteError($"💥 常駐プロセスが起動直後に終了しました。ログを確認してください: {DaemonState.GetLogPath(dataRoot)}");
                return false;
            }

            DaemonState? state = DaemonState.Load(dataRoot);
            if (state is { ServerPid: not null } && state.SupervisorPid == supervisor.Id)
            {
                ConsoleLogger.WriteSuccess($"🌐 常駐を開始しました: http://localhost:{options.Port} (PID {supervisor.Id})");
                ConsoleLogger.WriteInfo($"   ログ: {DaemonState.GetLogPath(dataRoot)}");
                return true;
            }

            await Task.Delay(200);
        }

        ConsoleLogger.WriteError("💥 常駐の開始を確認できませんでした（タイムアウト）。");
        return false;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using TcpListener listener = new(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}日{uptime.Hours}時間"
            : uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours}時間{uptime.Minutes}分"
            : $"{(int)uptime.TotalMinutes}分{uptime.Seconds}秒";
}
