using System.Net;

namespace SebastianCi.Web;

/// <summary>
/// 認証なしで起動しているときの Host ヘッダー検証。
/// 認証が無いと、悪意あるサイトが DNS リバインディングで「自分のドメイン名のまま localhost に到達」し、
/// 設定編集やパイプライン実行を操作できてしまう。Host が loopback 名のときだけ受け付けて防ぐ。
/// </summary>
public static class HostPolicy
{
    public static bool IsLoopbackHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;

        string host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0) return false;
            host = host[1..end];
        }
        else
        {
            int colon = host.LastIndexOf(':');
            if (colon >= 0) host = host[..colon];
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        return host == "localhost" || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address));
    }
}
