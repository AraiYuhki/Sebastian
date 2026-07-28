using System.Security.Cryptography;
using System.Text;

namespace SebastianCi.Web;

/// <summary>
/// ダッシュボード（serve）のトークン認証の判定だけを担当する。
/// Authorization ヘッダー（Bearer）・専用ヘッダー・クエリ文字列・Cookie のいずれかで
/// 正しいトークンが提示されていれば許可する。比較はタイミング攻撃を避けて一定時間で行う。
/// </summary>
public sealed class ServeAuthenticator
{
    public const string TokenHeaderName = "X-Sebastian-Token";
    public const string TokenQueryName = "token";
    public const string TokenCookieName = "sebastian_ci_token";
    private const string BearerPrefix = "Bearer ";

    private readonly string _token;

    public ServeAuthenticator(string token) => _token = token;

    public bool IsAuthorized(
        string? authorizationHeader, string? tokenHeader, string? queryToken, string? cookieToken)
        => MatchesBearer(authorizationHeader)
            || Matches(tokenHeader)
            || Matches(queryToken)
            || Matches(cookieToken);

    private bool MatchesBearer(string? authorizationHeader)
        => authorizationHeader is not null
            && authorizationHeader.StartsWith(BearerPrefix, StringComparison.Ordinal)
            && Matches(authorizationHeader[BearerPrefix.Length..]);

    private bool Matches(string? candidate)
        => candidate is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token));
}
