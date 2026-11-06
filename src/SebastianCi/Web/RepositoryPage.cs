using System.Net;

namespace SebastianCi.Web;

/// <summary>
/// ハブの `/r/{id}/` で配信するリポジトリ詳細ページ。単体 `serve` と同じダッシュボード HTML を、
/// API の宛先だけ `/r/{id}` 配下に付け替えて再利用する（画面コードを二重に持たない）。
/// </summary>
public static class RepositoryPage
{
    private const string WebSocketUrlTail = "+ \"/api/run/ws\"";
    private const string RepositoryLabel = "<span class=\"sub\" id=\"repo\"></span>";

    /// <param name="id">URL 用の ID（HubStore.IsValidId を通ったもの。英小文字・数字・ハイフンのみ）。</param>
    public static string Render(string id, string name)
    {
        if (!HubStore.IsValidId(id)) throw new ArgumentException("不正なリポジトリIDです。", nameof(id));

        string baseUrl = "/r/" + id;
        // fetch の宛先だけを付け替える。/api/me（ログイン中のユーザー）は全体で共通なので除外する。
        string script = "<script>(function(){var B=\"" + baseUrl + "\",f=window.fetch;"
            + "window.fetch=function(u,o){return f(typeof u===\"string\"&&u.charAt(0)===\"/\"&&u!==\"/api/me\"?B+u:u,o)};})();</script>";

        string html = DashboardPage.Html;
        html = Replace(html, "<head>", "<head>\n" + script);
        html = Replace(html, WebSocketUrlTail, "+ \"" + baseUrl + "/api/run/ws\"");
        html = Replace(html, RepositoryLabel,
            "<a href=\"/\" class=\"sub\" style=\"text-decoration:none\">← ハブ</a><strong>" + WebUtility.HtmlEncode(name) + "</strong>" + RepositoryLabel);
        return html;
    }

    /// <summary>ダッシュボード側の記述が変わって置換できなくなったら、気付けるよう例外にする（テストで検出）。</summary>
    private static string Replace(string source, string target, string replacement)
    {
        int index = source.IndexOf(target, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException($"ダッシュボードHTMLに '{target}' が見つかりません。");

        return source[..index] + replacement + source[(index + target.Length)..];
    }
}
