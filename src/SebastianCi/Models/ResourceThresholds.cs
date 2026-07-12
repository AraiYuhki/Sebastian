namespace SebastianCi.Models;

/// <summary>
/// リソース警告のしきい値（.sebastian-ci.yaml の resources 配下）。
/// 利用可能量がこの値を下回ると警告を出す。0 はそのチェックを無効化する。
/// </summary>
public sealed class ResourceThresholds
{
    /// <summary>空きメモリの警告しきい値（MB）。既定は 512MB。</summary>
    public int MinMemoryMb { get; set; } = 512;

    /// <summary>空きディスクの警告しきい値（MB）。既定は 1024MB。</summary>
    public int MinDiskMb { get; set; } = 1024;
}
