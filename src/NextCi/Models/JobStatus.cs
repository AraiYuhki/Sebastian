namespace NextCi.Models;

/// <summary>
/// ジョブの実行状態を表す。
/// </summary>
public enum JobStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped
}
