namespace SebastianCi.Models;

/// <summary>
/// 1ジョブの実行結果（識別子・最終状態・所要時間）を保持する。
/// </summary>
public sealed record JobResult(string JobId, JobStatus Status, TimeSpan Duration);
