namespace SebastianCi.Models;

/// <summary>
/// 1ジョブの実行結果（識別子・最終状態・所要時間・テストレポートの集計）を保持する。
/// Tests は reports 指定ジョブでレポートが見つかった場合のみセットされる。
/// </summary>
public sealed record JobResult(
    string JobId, JobStatus Status, TimeSpan Duration, TestReportSummary? Tests = null);
