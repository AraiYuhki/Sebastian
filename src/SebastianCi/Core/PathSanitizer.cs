using System.Security.Cryptography;
using System.Text;

namespace SebastianCi.Core;

/// <summary>
/// ジョブID等の任意文字列を、ファイル・ディレクトリ名として安全な形へ変換する処理だけを担当する。
/// マトリックス展開後のジョブID（例: test[image=repo/name:tag]）はパス区切り文字を含むため必須。
/// </summary>
public static class PathSanitizer
{
    /// <summary>
    /// 英数字と _ . - 以外を _ に置換する。置換が発生した場合は、
    /// 異なる入力が同名に潰れて衝突しないよう、元の文字列のハッシュ8桁を付与する。
    /// </summary>
    public static string ToFileSystemName(string value)
    {
        string sanitized = new(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '_').ToArray());
        if (sanitized == value) return value;

        string uniqueSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return $"{sanitized}-{uniqueSuffix}";
    }
}
