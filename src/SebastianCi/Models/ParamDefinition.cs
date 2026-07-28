namespace SebastianCi.Models;

/// <summary>
/// .sebastian-ci.yaml の params 配下1件分のスキーマを表す。
/// <code>
/// default:     省略時に使われる値（任意）。未指定のパラメーターは実行時に --param での指定が必須になる
/// description: パラメーターの説明（任意・表示用）
/// choices:     許可する値の一覧（任意）。指定すると、それ以外の値はエラーになる
/// </code>
/// </summary>
public sealed class ParamDefinition
{
    /// <summary>既定値。null（YAMLで未指定）の場合、このパラメーターは実行時指定が必須になる。</summary>
    public string? Default { get; set; }

    /// <summary>パラメーターの説明。エラーメッセージや一覧表示に使われる。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>許可する値の一覧。空なら任意の値を受け付ける。</summary>
    public List<string> Choices { get; set; } = new();
}
