using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace SebastianCi.Web;

public enum RequiredAccess { Anonymous, User, Admin }

/// <summary>
/// 「どのリクエストにどの権限が要るか」の唯一の定義。許可リスト方式で、載っていないものはすべて Admin 扱いにする
/// （新しいエンドポイントを足しても、うっかり一般ユーザーに開放されない）。
/// </summary>
public static class AccessPolicy
{
    /// <summary>ハブで各リポジトリの API を公開する接頭辞（/r/{id}）。ID は HubStore の規則（英小文字・数字・ハイフン）。</summary>
    private static readonly Regex RepositoryPrefix = new("^/r/[a-z0-9][a-z0-9-]{0,39}(?=/|$)", RegexOptions.Compiled);

    private static readonly string[] AnonymousPaths =
        ["/login", "/login/google", "/logout", "/signin-google", "/favicon.ico"];

    private static readonly string[] UserReadPaths =
    [
        "/", "/api/meta", "/api/me", "/api/resources", "/api/history", "/api/jobs",
        "/api/params", "/api/schedules", "/api/run", "/api/run/ws", "/hub/api/overview"
    ];

    private static readonly string[] UserReadPrefixes = ["/api/history/", "/api/logs/"];

    private static readonly string[] UserRunPaths = ["/api/run"];

    /// <summary>`/r/{id}/api/jobs` のようなハブ配下のパスを、単体 serve と同じ `/api/jobs` に正規化する。</summary>
    public static string StripRepositoryPrefix(string path)
    {
        Match match = RepositoryPrefix.Match(path);
        if (!match.Success) return path;

        string rest = path[match.Length..];
        return rest.Length == 0 ? "/" : rest;
    }

    /// <summary>API の要求か（未認証時に、画面なら /login へ誘導し、API なら 401 を返す判別用）。</summary>
    public static bool IsApiPath(string path)
    {
        string normalized = StripRepositoryPrefix(path);
        return normalized.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/hub/api/", StringComparison.OrdinalIgnoreCase);
    }

    public static RequiredAccess Required(string method, string path)
    {
        string stripped = StripRepositoryPrefix(path);
        // ログイン関連の匿名パスは、/r/{id} 配下では認めない（そこにはそのようなエンドポイントが無く、迷い込んだ要求は拒否側に倒す）。
        bool underRepository = stripped != path;
        if (underRepository && AnonymousPaths.Contains(stripped, StringComparer.OrdinalIgnoreCase)) return RequiredAccess.Admin;

        path = stripped;
        if (AnonymousPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return RequiredAccess.Anonymous;

        bool isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (isRead
            && (UserReadPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                || UserReadPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            return RequiredAccess.User;
        }

        if (HttpMethods.IsPost(method) && UserRunPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return RequiredAccess.User;
        }

        return RequiredAccess.Admin;
    }

    /// <summary>
    /// 承認ゲートの自動承認（--yes）を指定できるか。承認ゲートは人間の確認を求める仕組みなので、
    /// 実行だけが許された一般ユーザーには、それを素通りさせる権限を与えない。
    /// </summary>
    public static bool CanAutoApprove(AccessRole? role) => role == AccessRole.Admin;

    public static bool Satisfies(AccessRole? role, RequiredAccess required)
        => required switch
        {
            RequiredAccess.Anonymous => true,
            RequiredAccess.User => role is not null,
            _ => role == AccessRole.Admin
        };
}
