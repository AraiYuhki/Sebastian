namespace SebastianCi.Models;

/// <summary>
/// ある時点のマシンのリソース状況（メモリ・ディスクの総量と空き量、単位MB）。
/// </summary>
public sealed record SystemResourceSnapshot(
    long TotalMemoryMb,
    long AvailableMemoryMb,
    long TotalDiskMb,
    long AvailableDiskMb)
{
    /// <summary>空きメモリの割合（0.0〜1.0）。総量が不明な場合は 1.0 とみなす。</summary>
    public double MemoryFreeRatio => TotalMemoryMb > 0 ? (double)AvailableMemoryMb / TotalMemoryMb : 1.0;

    /// <summary>空きディスクの割合（0.0〜1.0）。総量が不明な場合は 1.0 とみなす。</summary>
    public double DiskFreeRatio => TotalDiskMb > 0 ? (double)AvailableDiskMb / TotalDiskMb : 1.0;
}
