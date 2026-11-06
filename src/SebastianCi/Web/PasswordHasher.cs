using System.Security.Cryptography;

namespace SebastianCi.Web;

/// <summary>
/// ローカルアカウントのパスワードを PBKDF2-SHA256（ソルト付き）で保存・検証する。
/// 保存形式: pbkdf2-sha256$反復回数$ソルト(Base64)$ハッシュ(Base64)。比較は一定時間で行う。
/// </summary>
public static class PasswordHasher
{
    public const int MinimumPasswordLength = 10;

    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 600_000;   // OWASP 推奨（PBKDF2-HMAC-SHA256）。保存済みハッシュは自分の反復回数で検証される
    private const int MaxAcceptedIterations = 5_000_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private static readonly string DummyHash = Hash("dummy-password-for-timing");

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        string[] parts = stored.Split('$');
        if (parts is not [Prefix, var iterationText, var saltText, var hashText]
            || !int.TryParse(iterationText, out int iterations)
            || iterations is < 1 or > MaxAcceptedIterations)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(saltText);
            byte[] expected = Convert.FromBase64String(hashText);
            // 空のソルト/ハッシュは「空同士が一致して常に成功」を招くため、users.json が壊れていても拒否する。
            if (salt.Length < SaltSize || expected.Length < HashSize) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>存在しないユーザーでも検証と同程度の時間を使い、応答時間でユーザーの有無が分からないようにする。</summary>
    public static void SpendVerificationTime(string password) => Verify(password, DummyHash);
}
