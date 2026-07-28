namespace SebastianCi.Core;

/// <summary>
/// ホスト環境変数から展開された秘密情報（APIキー・トークンなど）の値を記録し、
/// ジョブ出力・ログに現れた際に伏せ字へ置き換える。プロセス全体で共有されるレジストリ。
/// 誤検知を避けるため、ごく短い値（4文字未満）はマスク対象にしない。
/// </summary>
public static class SecretMasker
{
    public const string MaskText = "****";
    private const int MinimumSecretLength = 4;

    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);

    /// <summary>値をマスク対象として登録する。空・短すぎる値は無視する。</summary>
    public static void Register(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinimumSecretLength) return;

        lock (SyncRoot)
        {
            Secrets.Add(value);
        }
    }

    /// <summary>登録済みの値をすべて伏せ字に置き換えた文字列を返す。長い値から順に置換する。</summary>
    public static string Mask(string line)
    {
        if (line.Length == 0) return line;

        string[] snapshot;
        lock (SyncRoot)
        {
            if (Secrets.Count == 0) return line;
            snapshot = Secrets.OrderByDescending(secret => secret.Length).ToArray();
        }

        foreach (string secret in snapshot)
        {
            line = line.Replace(secret, MaskText, StringComparison.Ordinal);
        }

        return line;
    }

    /// <summary>登録済みの値をすべて破棄する（テスト用）。</summary>
    public static void Clear()
    {
        lock (SyncRoot)
        {
            Secrets.Clear();
        }
    }
}
