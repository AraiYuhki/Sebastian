namespace SebastianCi.Models;

/// <summary>
/// コミットハッシュと、そのビルド結果を1件にまとめた履歴エントリ（一覧表示用）。
/// </summary>
public sealed record HistoryEntry(string CommitHash, BuildRecord Record);
