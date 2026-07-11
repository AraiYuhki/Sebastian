namespace SebastianCi.Models;

/// <summary>
/// history.json に記録する、1ジョブ分の実行結果サマリー。
/// </summary>
public sealed record JobRecord(string JobId, JobStatus Status, double DurationSeconds);
