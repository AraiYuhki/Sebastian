namespace SebastianCi.Models;

/// <summary>
/// history.json に記録する、1ジョブ分の実行結果サマリー。
/// Tests は reports 指定ジョブでレポートが見つかった場合のみ記録される。
/// </summary>
public sealed record JobRecord(
    string JobId, JobStatus Status, double DurationSeconds, TestReportSummary? Tests = null);
