using System.Text.RegularExpressions;

namespace SebastianCi.Core;

/// <summary>
/// env の値に含まれる $NAME / ${NAME} 形式のプレースホルダーを、
/// 実行マシンの環境変数（Environment.GetEnvironmentVariable）で置換する処理だけを担当する。
/// $$ はエスケープとしてリテラルの $ に置換される。
/// </summary>
public static partial class EnvironmentVariableExpander
{
    [GeneratedRegex(@"\$\$|\$\{(\w+)\}|\$(\w+)")]
    private static partial Regex PlaceholderPattern();

    /// <summary>プレースホルダーを展開した値を返す。参照先が未定義の場合は例外をスローする。</summary>
    public static string Expand(string value, string ownerLabel)
        => PlaceholderPattern().Replace(value, match => ResolvePlaceholder(match, ownerLabel));

    private static string ResolvePlaceholder(Match match, string ownerLabel)
    {
        if (match.Value is "$$") return "$";

        string variableName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return Environment.GetEnvironmentVariable(variableName)
            ?? throw new InvalidPipelineException(
                $"{ownerLabel} が参照するホスト環境変数 '{variableName}' が定義されていません。");
    }
}
