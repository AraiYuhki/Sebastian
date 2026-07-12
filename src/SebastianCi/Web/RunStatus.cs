namespace SebastianCi.Web;

/// <summary>
/// 実行状態のスナップショット（実行中か・直近の終了コード・これまでの出力行）。
/// </summary>
public sealed record RunStatus(bool IsRunning, int? ExitCode, IReadOnlyList<string> OutputLines);
