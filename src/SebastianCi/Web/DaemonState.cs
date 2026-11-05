using System.Diagnostics;
using System.Text.Json;

namespace SebastianCi.Web;

/// <summary>
/// 常駐スーパーバイザーの状態（daemon.json）。`daemon status` / `stop` が稼働中の本体を特定するのに使う。
/// PID の再利用で無関係なプロセスを止めないよう、プロセス開始時刻も一緒に記録して照合する。
/// </summary>
public sealed record DaemonState(
    int SupervisorPid,
    DateTimeOffset SupervisorStartedAt,
    int Port,
    string RepositoryPath,
    int? ServerPid,
    int RestartCount,
    int? LastExitCode,
    DateTimeOffset? LastRestartAt)
{
    public const string StateFileName = "daemon.json";
    public const string LogFileName = "daemon.log";

    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetStatePath(string dataRootPath) => Path.Combine(dataRootPath, StateFileName);

    public static string GetLogPath(string dataRootPath) => Path.Combine(dataRootPath, LogFileName);

    /// <summary>状態ファイルを読む。無い・壊れている場合は null。</summary>
    public static DaemonState? Load(string dataRootPath)
    {
        try
        {
            string path = GetStatePath(dataRootPath);
            return File.Exists(path) ? JsonSerializer.Deserialize<DaemonState>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>一時ファイル経由で置き換え、読み手が書きかけを見ないようにする。</summary>
    public void Save(string dataRootPath)
    {
        Directory.CreateDirectory(dataRootPath);
        string path = GetStatePath(dataRootPath);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static void Delete(string dataRootPath)
    {
        try { File.Delete(GetStatePath(dataRootPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>記録されたスーパーバイザーが今も生きている（同一プロセス）か。</summary>
    public bool IsSupervisorAlive()
    {
        try
        {
            using Process process = Process.GetProcessById(SupervisorPid);
            return !process.HasExited
                && (process.StartTime.ToUniversalTime() - SupervisorStartedAt.UtcDateTime).Duration() <= StartTimeTolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
