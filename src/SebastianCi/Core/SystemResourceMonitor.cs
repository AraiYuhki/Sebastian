using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// マシンのメモリ・ディスク使用状況の取得と、しきい値に基づく警告表示だけを担当する。
/// </summary>
public sealed class SystemResourceMonitor
{
    private const long BytesPerMb = 1024 * 1024;
    private const string MemInfoPath = "/proc/meminfo";

    private readonly ResourceThresholds _thresholds;

    public SystemResourceMonitor(ResourceThresholds thresholds) => _thresholds = thresholds;

    /// <summary>指定パスが載るドライブとメモリの現在の状況を取得する。</summary>
    public SystemResourceSnapshot Capture(string path)
    {
        (long totalMemoryMb, long availableMemoryMb) = ReadMemory();
        (long totalDiskMb, long availableDiskMb) = ReadDisk(path);
        return new SystemResourceSnapshot(totalMemoryMb, availableMemoryMb, totalDiskMb, availableDiskMb);
    }

    /// <summary>現在のリソース状況をコンソールに表示し、しきい値を下回っていれば警告する。</summary>
    public SystemResourceSnapshot ReportAndWarn(string path)
    {
        SystemResourceSnapshot snapshot = Capture(path);
        ConsoleLogger.WriteInfo(
            $"🖥 リソース: メモリ {snapshot.AvailableMemoryMb}/{snapshot.TotalMemoryMb} MB 空き, " +
            $"ディスク {snapshot.AvailableDiskMb}/{snapshot.TotalDiskMb} MB 空き");
        WarnIfLow("メモリ", snapshot.AvailableMemoryMb, _thresholds.MinMemoryMb);
        WarnIfLow("ディスク", snapshot.AvailableDiskMb, _thresholds.MinDiskMb);
        return snapshot;
    }

    private static void WarnIfLow(string label, long availableMb, int thresholdMb)
    {
        if (thresholdMb <= 0 || availableMb >= thresholdMb) return;

        ConsoleLogger.WriteWarning(
            $"⚠ {label}の空きが少なくなっています（残り {availableMb} MB / しきい値 {thresholdMb} MB）。");
    }

    private static (long TotalMb, long AvailableMb) ReadMemory()
    {
        if (!File.Exists(MemInfoPath)) return (0, 0);

        Dictionary<string, long> values = ParseMemInfo(File.ReadLines(MemInfoPath));
        long totalKb = values.GetValueOrDefault("MemTotal");
        long availableKb = values.GetValueOrDefault("MemAvailable");
        return (totalKb / 1024, availableKb / 1024);
    }

    private static Dictionary<string, long> ParseMemInfo(IEnumerable<string> lines)
    {
        Dictionary<string, long> values = new();
        foreach (string line in lines)
        {
            string[] parts = line.Split(':', 2);
            if (parts.Length == 2 && long.TryParse(ExtractNumber(parts[1]), out long kb))
            {
                values[parts[0].Trim()] = kb;
            }
        }

        return values;
    }

    private static string ExtractNumber(string value) => value.Trim().Split(' ', 2)[0];

    private static (long TotalMb, long AvailableMb) ReadDisk(string path)
    {
        try
        {
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(path)) ?? "/");
            return (drive.TotalSize / BytesPerMb, drive.AvailableFreeSpace / BytesPerMb);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return (0, 0);
        }
    }
}
