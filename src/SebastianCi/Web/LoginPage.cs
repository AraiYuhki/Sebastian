using System.Net;

namespace SebastianCi.Web;

/// <summary>ログイン画面の HTML。エラー表示は固定メッセージだけで、リクエスト由来の文字列は出力しない。</summary>
public static class LoginPage
{
    public static string Render(bool showPasswordForm, bool showGoogle, string? errorCode)
    {
        string error = errorCode switch
        {
            "invalid" => "ユーザー名またはパスワードが違います。",
            "throttle" => "ログインの失敗が続いたため、しばらくしてからやり直してください。",
            "forbidden" => "このGoogleアカウントにはアクセス権がありません。管理者に登録を依頼してください。",
            "google-failed" => "Googleログインに失敗しました。もう一度お試しください。",
            _ => ""
        };

        string errorHtml = error.Length == 0 ? "" : $"<p class=\"err\">{WebUtility.HtmlEncode(error)}</p>";
        string passwordForm = showPasswordForm
            ? """
              <form method="post" action="/login">
                <label>ユーザー名<input name="username" autocomplete="username" required autofocus></label>
                <label>パスワード<input name="password" type="password" autocomplete="current-password" required></label>
                <button type="submit">ログイン</button>
              </form>
              """
            : "";
        string divider = showPasswordForm && showGoogle ? "<div class=\"or\">または</div>" : "";
        string google = showGoogle ? "<a class=\"google\" href=\"/login/google\">Google でログイン</a>" : "";
        string none = !showPasswordForm && !showGoogle
            ? "<p class=\"err\">利用できるログイン方法がありません。`sebastian-ci auth` でアカウントを登録してください。</p>"
            : "";

        return $$"""
            <!doctype html>
            <html lang="ja"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>ログイン - sebastian-ci</title>
            <style>
              :root { --bg:#f5f6f8; --card:#fff; --fg:#1f2430; --line:#d9dce3; --accent:#2f6fed; --ng:#c62828; }
              @media (prefers-color-scheme: dark) { :root { --bg:#14161b; --card:#1d2027; --fg:#e6e8ee; --line:#333845; --accent:#5b8cff; --ng:#ef7a7a; } }
              body { margin:0; min-height:100vh; display:grid; place-items:center; background:var(--bg); color:var(--fg); font:15px system-ui,sans-serif; }
              main { width:min(360px,92vw); background:var(--card); border:1px solid var(--line); border-radius:10px; padding:28px; }
              h1 { font-size:20px; margin:0 0 20px; }
              label { display:block; font-size:13px; margin-bottom:12px; }
              input { display:block; width:100%; box-sizing:border-box; margin-top:4px; padding:9px 10px; font:inherit; color:inherit; background:transparent; border:1px solid var(--line); border-radius:6px; }
              button, .google { display:block; width:100%; box-sizing:border-box; text-align:center; padding:10px; font:inherit; border-radius:6px; cursor:pointer; text-decoration:none; }
              button { background:var(--accent); color:#fff; border:0; }
              .google { color:var(--fg); border:1px solid var(--line); }
              .or { text-align:center; font-size:12px; opacity:.6; margin:14px 0; }
              .err { color:var(--ng); font-size:13px; margin:0 0 14px; }
            </style></head>
            <body><main>
              <h1>🛠 sebastian-ci</h1>
              {{errorHtml}}{{passwordForm}}{{divider}}{{google}}{{none}}
            </main></body></html>
            """;
    }
}
