namespace SebastianCi.Models;

/// <summary>
/// ジョブの実行状態を表す。
/// </summary>
public enum JobStatus
{
    Pending,
    Running,
    Success,
    Failed,

    /// <summary>先行ジョブの失敗により実行されなかった。</summary>
    Skipped,

    /// <summary>changes のパターンに一致する変更がなかったため実行不要と判定された。</summary>
    SkippedByChanges,

    /// <summary>失敗したが continue-on-error 指定のため、後続と全体成否に影響させないもの。</summary>
    FailedIgnored
}
