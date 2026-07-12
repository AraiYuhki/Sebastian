namespace SebastianCi.Web;

/// <summary>設定検証の結果（成否と、表示用の出力メッセージ）。</summary>
public sealed record ValidationResult(bool IsValid, string Output);
