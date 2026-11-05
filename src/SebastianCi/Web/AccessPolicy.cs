using Microsoft.AspNetCore.Http;

namespace SebastianCi.Web;

public enum RequiredAccess { Anonymous, User, Admin }

/// <summary>
/// 「どのリクエストにどの権限が要るか」の唯一の定義。許可リスト方式で、載っていないものはすべて Admin 扱いにする
/// （新しいエンドポイントを足しても、うっかり一般ユーザーに開放されない）。
/// </summary>
public static class AccessPolicy
{
    private static readonly string[] AnonymousPaths =
        ["/login", "/login/google", "/logout", "/signin-google", "/favicon.ico"];

    private static readonly string[] UserReadPaths =
    [
        "/", "/api/meta", "/api/me", "/api/resources", "/api/history", "/api/jobs",
        "/api/params", "/api/schedules", "/api/run", "/api/run/ws"
    ];

    private static readonly string[] UserReadPrefixes = ["/api/history/", "/api/logs/"];

    private static readonly string[] UserRunPaths = ["/api/run"];

    public static RequiredAccess Required(string method, string path)
    {
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

    public static bool Satisfies(AccessRole? role, RequiredAccess required)
        => required switch
        {
            RequiredAccess.Anonymous => true,
            RequiredAccess.User => role is not null,
            _ => role == AccessRole.Admin
        };
}
