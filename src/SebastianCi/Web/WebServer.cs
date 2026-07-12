using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Web;

/// <summary>
/// ダッシュボードを提供するローカルWebサーバーの構築・起動だけを担当する。
/// 常駐サーバーではなく「serve コマンドを実行している間だけ」動く。
/// </summary>
public sealed class WebServer
{
    private readonly ServeOptions _options;
    private readonly string _repositoryPath;
    private readonly string _dataRootPath;

    public WebServer(ServeOptions options)
    {
        _options = options;
        _repositoryPath = Path.GetFullPath(options.RepositoryPath);
        _dataRootPath = options.DataDirectoryPath is null
            ? Path.Combine(_repositoryPath, HistoryManager.DefaultDataDirectoryName)
            : Path.GetFullPath(options.DataDirectoryPath);
    }

    /// <summary>サーバーを起動し、停止要求（Ctrl+C）まで待機する。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        WebApplication app = BuildApplication();
        string url = $"http://localhost:{_options.Port}";
        ConsoleLogger.WriteSuccess($"🌐 ダッシュボードを起動しました: {url}");
        ConsoleLogger.WriteInfo("   停止するには Ctrl+C を押してください。");
        await app.RunAsync(url);
    }

    private WebApplication BuildApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
            jsonOptions.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter()));
        WebApplication app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/meta", () => Results.Json(new { repositoryPath = _repositoryPath }));
        app.MapGet("/api/resources", GetResources);
        app.MapGet("/api/config", GetConfig);
        app.MapGet("/api/history", GetHistoryAsync);
    }

    private IResult GetResources()
    {
        SystemResourceSnapshot snapshot = new SystemResourceMonitor(new()).Capture(_repositoryPath);
        return Results.Json(snapshot);
    }

    private IResult GetConfig()
    {
        string configPath = Path.Combine(_repositoryPath, _options.ConfigFileName);
        string content = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        return Results.Json(new { fileName = _options.ConfigFileName, content });
    }

    private async Task<IResult> GetHistoryAsync(CancellationToken cancellationToken)
    {
        HistoryManager historyManager = new(_dataRootPath);
        return Results.Json(await historyManager.ReadAllRecordsAsync(cancellationToken));
    }
}
