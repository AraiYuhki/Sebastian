using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>
/// ダッシュボードを提供するローカルWebサーバーの構築・起動だけを担当する。
/// 通常は `serve` を実行している間だけ動き、`daemon` で常駐させることもできる。
/// 単体モードは1リポジトリ、ハブモード（--hub）は登録された複数リポジトリを1つの画面で扱う。
/// </summary>
public sealed class WebServer
{
    private readonly ServeOptions _options;
    private readonly AccessControl _accessControl;
    private readonly RepositoryApi? _single;
    private readonly HubService? _hub;

    public WebServer(ServeOptions options)
    {
        _options = options;
        _accessControl = new AccessControl(options);
        if (options.Hub)
        {
            _hub = new HubService(options, _accessControl);
            return;
        }

        string repositoryPath = Path.GetFullPath(options.RepositoryPath);
        _single = new RepositoryApi(
            Path.GetFileName(repositoryPath.TrimEnd('/', '\\')), repositoryPath, options.ResolveDataRootPath(),
            options.ConfigFileName, _accessControl);
    }

    /// <summary>サーバーを起動し、停止要求（Ctrl+C）まで待機する。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        WebApplication app = BuildApplication();
        string url = $"http://localhost:{_options.Port}";
        ConsoleLogger.WriteSuccess(_hub is null
            ? $"🌐 ダッシュボードを起動しました: {url}"
            : $"🌐 ハブ（複数リポジトリのダッシュボード）を起動しました: {url}");
        ReportAuthentication(url);
        ConsoleLogger.WriteInfo("   停止するには Ctrl+C を押してください。");
        Task backgroundTask = _hub is null
            ? _single!.RunSchedulesAsync(app.Lifetime.ApplicationStopping)
            : _hub.RunAsync(app.Lifetime.ApplicationStopping);
        await app.RunAsync(url);
        await backgroundTask;
    }

    private void ReportAuthentication(string url)
    {
        _accessControl.Describe(ConsoleLogger.WriteInfo, ConsoleLogger.WriteWarning);
        if (_options.Token is not null)
        {
            ConsoleLogger.WriteInfo($"   --token 利用時は {url}/?token=<トークン> でもアクセスできます（管理者権限）。");
        }

        if (_hub is not null)
        {
            ConsoleLogger.WriteInfo($"   登録内容の保存先: {_hub.DataRootPath}");
        }
    }

    private WebApplication BuildApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
            jsonOptions.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddSingleton<IRepositoryResolver>(
            _hub is not null ? _hub : new SingleRepositoryResolver(_single!));
        _accessControl.ConfigureServices(builder.Services);
        WebApplication app = builder.Build();
        app.UseWebSockets();
        _accessControl.UseAccessControl(app);
        _accessControl.MapEndpoints(app);
        MapEndpoints(app);
        return app;
    }

    private void MapEndpoints(WebApplication app)
    {
        if (_hub is null)
        {
            app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html; charset=utf-8"));
            RepositoryApi.MapEndpoints(app, "");
            return;
        }

        HubApi hubApi = new(_hub, _accessControl);
        app.Use(hubApi.RequireRepositoryAsync);
        hubApi.Map(app);
    }
}
