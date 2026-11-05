using System.Net;

namespace SebastianCi.Web;

/// <summary>
/// X-Forwarded-* ヘッダーを信頼するプロキシの一覧。SEBASTIAN_CI_TRUST_PROXY=1 のときだけ使う。
/// 既定は loopback（同一ホストのリバースプロキシ）だけ。別ホストのプロキシは
/// SEBASTIAN_CI_TRUSTED_PROXIES にカンマ区切りの IP で明示する（信頼を無制限にすると X-Forwarded-For を偽装でき、
/// IP 単位のログイン制限を回避されるため）。
/// </summary>
public static class TrustedProxies
{
    public const string EnvironmentVariable = "SEBASTIAN_CI_TRUSTED_PROXIES";

    public static IReadOnlyList<IPAddress> Parse(string? commaSeparated)
    {
        List<IPAddress> proxies = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        foreach (string text in (commaSeparated ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(text, out IPAddress? address) && !proxies.Contains(address)) proxies.Add(address);
        }

        return proxies;
    }
}
