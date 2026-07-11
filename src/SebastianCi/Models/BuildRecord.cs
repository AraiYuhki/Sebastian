namespace SebastianCi.Models;

/// <summary>
/// history.json に保存する、コミットハッシュ1件分の実行メタデータ
/// （実行日時・成否・ログディレクトリへのパス・ジョブ別結果）。
/// </summary>
public sealed record BuildRecord(
    DateTimeOffset ExecutedAt,
    bool IsSuccess,
    string LogDirectoryPath,
    List<JobRecord> Jobs);
