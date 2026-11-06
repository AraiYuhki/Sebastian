namespace SebastianCi.Core;

/// <summary>
/// 実行ログに出す、機械が読むための目印。ハブが「実際には何も実行されなかった」実行を見分けるのに使う
/// （文字コードに左右されないよう ASCII にしている）。
/// </summary>
public static class RunMarkers
{
    /// <summary>このコミットは実行済みのためスキップした（ジョブは1つも実行していない）。</summary>
    public const string AlreadyBuilt = "[skipped:already-built]";
}
