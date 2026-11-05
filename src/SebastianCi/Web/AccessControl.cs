using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SebastianCi.Web;

/// <summary>
/// ダッシュボードのアクセス制御（ログイン・権限判定）を担当する。
/// 認証手段は (1) ユーザー名 + パスワード (2) Google ログイン (3) 従来の --token（管理者相当・API 用）。
/// ロールは Admin（設定の編集まで）と User（閲覧と実行まで）。
/// 認証が必要かどうかは起動時に確定する（実行中にアカウントを全削除しても、無認証で開放されない）。
/// </summary>
public sealed class AccessControl
{
    public const string GoogleEnvClientId = "SEBASTIAN_CI_GOOGLE_CLIENT_ID";
    public const string GoogleEnvClientSecret = "SEBASTIAN_CI_GOOGLE_CLIENT_SECRET";
    public const string TrustProxyEnvironmentVariable = "SEBASTIAN_CI_TRUST_PROXY";
    public const string LocalKind = "local";
    public const string GoogleKind = "google";

    private const string CookieScheme = "SebastianSession";
    private const string KindClaim = "sebastian_kind";
    private const string GoogleScheme = GoogleDefaults.AuthenticationScheme;

    private readonly ServeOptions _options;
    private readonly UserStore _store;
    private readonly ServeAuthenticator? _tokenAuthenticator;
    private readonly string? _googleClientId = Environment.GetEnvironmentVariable(GoogleEnvClientId);
    private readonly string? _googleClientSecret = Environment.GetEnvironmentVariable(GoogleEnvClientSecret);
    private readonly LoginThrottle _throttle = new(maxFailures: 5, window: TimeSpan.FromMinutes(10));
    private readonly LoginThrottle _ipThrottle = new(maxFailures: 30, window: TimeSpan.FromMinutes(10));

    public AccessControl(ServeOptions options)
    {
        _options = options;
        _store = new UserStore(options.ResolveDataRootPath());
        _tokenAuthenticator = options.Token is null ? null : new ServeAuthenticator(options.Token);
        IsGoogleEnabled = !string.IsNullOrWhiteSpace(_googleClientId) && !string.IsNullOrWhiteSpace(_googleClientSecret);
        IsEnabled = options.Token is not null || !_store.Current.IsEmpty;
    }

    /// <summary>ログインを要求するか。トークンもアカウントも無ければ従来どおり無認証。</summary>
    public bool IsEnabled { get; }

    public bool IsGoogleEnabled { get; }

    public void Describe(Action<string> info, Action<string> warning)
    {
        if (!IsEnabled)
        {
            warning("⚠ 認証なしで起動しています。localhost 以外に公開する場合は `auth add-user` などでアカウントを登録してください。");
            return;
        }

        UserData data = _store.Current;
        info($"🔑 認証が有効です（ローカルアカウント {data.LocalUsers.Count} 件"
            + $"、Google 管理者 {data.GoogleAdmins.Count} 件、許可ドメイン {data.GoogleDomains.Count} 件"
            + (_options.Token is null ? "" : "、--token") + "）。");
        if (data.GoogleAdmins.Count + data.GoogleDomains.Count > 0 && !IsGoogleEnabled)
        {
            warning($"⚠ Google の許可設定がありますが、{GoogleEnvClientId} / {GoogleEnvClientSecret} が未設定のため Google ログインは使えません。");
        }

        if (IsGoogleEnabled && data.GoogleAdmins.Count + data.GoogleDomains.Count == 0)
        {
            warning("⚠ Google ログインは有効ですが、許可するメール/ドメインが未登録のため誰もログインできません（`auth add-admin` / `auth add-domain`）。");
        }
    }

    // ---- サービス・ミドルウェア・エンドポイントの組み込み ----

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName("sebastian-ci")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_options.ResolveDataRootPath(), "keys")));

        AuthenticationBuilder authentication = services.AddAuthentication(CookieScheme)
            .AddCookie(CookieScheme, ConfigureCookie);
        if (IsGoogleEnabled) authentication.AddGoogle(GoogleScheme, ConfigureGoogle);
    }

    public void UseAccessControl(WebApplication app)
    {
        if (Environment.GetEnvironmentVariable(TrustProxyEnvironmentVariable) == "1")
        {
            app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
            {
                ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                    | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                    | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
                KnownNetworks = { },
                KnownProxies = { }
            });
        }

        if (!IsEnabled) app.Use(GuardOpenDashboardAsync);

        app.UseAuthentication();
        app.Use(AuthorizeAsync);
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/login", ShowLogin);
        app.MapPost("/login", (Delegate)LoginAsync);
        app.MapGet("/login/google", (HttpContext _) => IsGoogleEnabled
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [GoogleScheme])
            : Results.Redirect("/login"));
        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieScheme);
            return Results.Redirect("/login");
        });
        app.MapGet("/api/me", GetMe);
    }

    // ---- 認可 ----

    /// <summary>
    /// 認証なしで動いているときの防御。Host が loopback 名でない要求（DNS リバインディング）と、
    /// 他オリジンからの書き込み要求を拒否する。認証ありのときは資格情報が防御になるので対象外。
    /// </summary>
    private static async Task GuardOpenDashboardAsync(HttpContext context, RequestDelegate next)
    {
        if (!HostPolicy.IsLoopbackHost(context.Request.Host.Value) || !IsSameOriginForWrite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("認証なしで起動しているため、localhost 以外の Host / 他オリジンからのリクエストは受け付けません。");
            return;
        }

        await next(context);
    }

    private async Task AuthorizeAsync(HttpContext context, RequestDelegate next)
    {
        RequiredAccess required = AccessPolicy.Required(context.Request.Method, context.Request.Path.Value ?? "/");
        if (!IsEnabled || required == RequiredAccess.Anonymous)
        {
            await next(context);
            return;
        }

        if (!IsSameOriginForWrite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("別のオリジンからのリクエストは受け付けません。");
            return;
        }

        AccessRole? role = ResolveRole(context);
        if (AccessPolicy.Satisfies(role, required))
        {
            await next(context);
            return;
        }

        if (role is not null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("この操作には管理者権限が必要です。");
            return;
        }

        bool isApi = context.Request.Path.StartsWithSegments("/api");
        if (isApi)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("認証が必要です。");
            return;
        }

        context.Response.Redirect("/login");
    }

    /// <summary>セッション Cookie（アカウント）または従来のトークン（管理者相当）からロールを決める。</summary>
    private AccessRole? ResolveRole(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true) return RoleOf(context.User);
        return IsTokenAuthorized(context) ? AccessRole.Admin : null;
    }

    private bool IsTokenAuthorized(HttpContext context)
    {
        if (_tokenAuthenticator is null) return false;

        string? queryToken = context.Request.Query[ServeAuthenticator.TokenQueryName];
        bool authorized = _tokenAuthenticator.IsAuthorized(
            context.Request.Headers.Authorization,
            context.Request.Headers[ServeAuthenticator.TokenHeaderName],
            queryToken,
            context.Request.Cookies[ServeAuthenticator.TokenCookieName]);
        if (authorized && queryToken is not null)
        {
            context.Response.Cookies.Append(ServeAuthenticator.TokenCookieName, queryToken,
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
        }

        return authorized;
    }

    /// <summary>状態を変える要求は、Origin ヘッダーがあれば自サイトのものに限る（CSRF の多重防御）。</summary>
    private static bool IsSameOriginForWrite(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return true;

        string? origin = request.Headers.Origin;
        if (string.IsNullOrEmpty(origin)) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static AccessRole? RoleOf(ClaimsPrincipal principal)
        => Enum.TryParse(principal.FindFirstValue(ClaimTypes.Role), ignoreCase: true, out AccessRole role) ? role : null;

    // ---- Cookie / Google 設定 ----

    private void ConfigureCookie(CookieAuthenticationOptions cookie)
    {
        cookie.Cookie.Name = "sebastian_ci_session";
        cookie.Cookie.HttpOnly = true;
        // Google からの戻り（他サイト起点のリダイレクト）でも Cookie を送らせるため Strict ではなく Lax にする。
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
        cookie.SlidingExpiration = true;
        cookie.Events.OnValidatePrincipal = async context =>
        {
            // アカウントの削除・ロール変更を、発行済みセッションにも反映する。
            string kind = context.Principal?.FindFirstValue(KindClaim) ?? LocalKind;
            string? name = context.Principal?.Identity?.Name;
            AccessRole? current = name is null ? null : _store.Current.ResolveSession(kind, name);
            if (current is null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieScheme);
            }
            else if (current != RoleOf(context.Principal!))
            {
                context.ReplacePrincipal(BuildPrincipal(name!, current.Value, kind));
                context.ShouldRenew = true;
            }
        };
    }

    private void ConfigureGoogle(GoogleOptions google)
    {
        google.SignInScheme = CookieScheme;
        google.ClientId = _googleClientId!;
        google.ClientSecret = _googleClientSecret!;
        google.CallbackPath = "/signin-google";
        google.ClaimActions.MapJsonKey("email_verified", "email_verified");
        google.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect("/login?error=google-failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };
        google.Events.OnTicketReceived = context =>
        {
            string? email = context.Principal?.FindFirstValue(ClaimTypes.Email);
            bool verified = string.Equals(
                context.Principal?.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
            AccessRole? role = _store.Current.ResolveGoogle(email, verified);
            if (role is null)
            {
                context.Response.Redirect("/login?error=forbidden");
                context.HandleResponse();
                return Task.CompletedTask;
            }

            context.Principal = BuildPrincipal(UserData.NormalizeEmail(email)!, role.Value, GoogleKind);
            return Task.CompletedTask;
        };
    }

    private static ClaimsPrincipal BuildPrincipal(string name, AccessRole role, string kind)
    {
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role.ToString().ToLowerInvariant()),
                new Claim(KindClaim, kind)
            ],
            CookieScheme);
        return new ClaimsPrincipal(identity);
    }

    // ---- ログイン画面・API ----

    private IResult ShowLogin(HttpContext context)
    {
        if (!IsEnabled || context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");

        bool hasLocal = !_store.Current.LocalUsers.IsEmpty();
        return Results.Content(
            LoginPage.Render(hasLocal, IsGoogleEnabled, context.Request.Query["error"]), "text/html; charset=utf-8");
    }

    private async Task<IResult> LoginAsync(HttpContext context)
    {
        if (!IsEnabled) return Results.Redirect("/");

        IFormCollection form = await context.Request.ReadFormAsync();
        string username = form["username"].ToString().Trim();
        string password = form["password"].ToString();
        string ipKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string userKey = username.ToLowerInvariant();

        if (_ipThrottle.IsBlocked(ipKey) || _throttle.IsBlocked(userKey))
        {
            return Results.Redirect("/login?error=throttle");
        }

        AccessRole? role = _store.Current.Authenticate(username, password);
        if (role is null)
        {
            _ipThrottle.RecordFailure(ipKey);
            _throttle.RecordFailure(userKey);
            return Results.Redirect("/login?error=invalid");
        }

        _throttle.Reset(userKey);
        string canonicalName = _store.Current.FindLocalUser(username)!.Username;
        await context.SignInAsync(CookieScheme, BuildPrincipal(canonicalName, role.Value, LocalKind));
        return Results.Redirect("/");
    }

    private IResult GetMe(HttpContext context)
    {
        if (!IsEnabled) return Results.Json(new { name = "local", role = "admin", kind = "none", authEnabled = false });

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return Results.Json(new
            {
                name = context.User.Identity.Name,
                role = RoleOf(context.User)?.ToString().ToLowerInvariant(),
                kind = context.User.FindFirstValue(KindClaim),
                authEnabled = true
            });
        }

        return Results.Json(new { name = "token", role = "admin", kind = "token", authEnabled = true });
    }
}

internal static class ListExtensions
{
    public static bool IsEmpty<T>(this List<T> list) => list.Count == 0;
}
