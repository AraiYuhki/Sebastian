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
    private readonly RunManager _runManager;
    private readonly ConfigEditor _configEditor;

    public WebServer(ServeOptions options)
    {
        _options = options;
        _repositoryPath = Path.GetFullPath(options.RepositoryPath);
        _dataRootPath = options.DataDirectoryPath is null
            ? Path.Combine(_repositoryPath, HistoryManager.DefaultDataDirectoryName)
            : Path.GetFullPath(options.DataDirectoryPath);
        _runManager = new RunManager(_repositoryPath);
        _configEditor = new ConfigEditor(_repositoryPath, options.ConfigFileName);
    }

    /// <summary>サーバーを起動し、停止要求（Ctrl+C）まで待機する。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        WebApplication app = BuildApplication();
        string url = $"http://localhost:{_options.Port}";
        ConsoleLogger.WriteSuccess($"🌐 ダッシュボードを起動しました: {url}");
        ReportAuthentication(url);
        ConsoleLogger.WriteInfo("   停止するには Ctrl+C を押してください。");
        await app.RunAsync(url);
    }

    private void ReportAuthentication(string url)
    {
        if (_options.Token is null)
        {
            ConsoleLogger.WriteWarning(
                "⚠ トークン認証なしで起動しています。localhost 以外に公開する場合は --token を指定してください。");
            return;
        }

        ConsoleLogger.WriteInfo($"🔑 トークン認証が有効です。ブラウザでは {url}/?token=<トークン> でアクセスしてください。");
    }

    private WebApplication BuildApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
            jsonOptions.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter()));
        WebApplication app = builder.Build();
        UseTokenAuthentication(app);
        app.UseWebSockets();
        MapEndpoints(app);
        return app;
    }

    /// <summary>
    /// --token 指定時のみ、全リクエストにトークン認証を課す。
    /// 初回はクエリ文字列（?token=）で受け取り、以降のAPI・WebSocket呼び出しが素通しにならないよう
    /// HttpOnly Cookie に引き継ぐ。ヘッダー（Bearer / X-Sebastian-Token）でも指定できる。
    /// </summary>
    private void UseTokenAuthentication(WebApplication app)
    {
        if (_options.Token is null) return;

        ServeAuthenticator authenticator = new(_options.Token);
        app.Use(async (context, next) =>
        {
            string? queryToken = context.Request.Query[ServeAuthenticator.TokenQueryName];
            bool authorized = authenticator.IsAuthorized(
                context.Request.Headers.Authorization,
                context.Request.Headers[ServeAuthenticator.TokenHeaderName],
                queryToken,
                context.Request.Cookies[ServeAuthenticator.TokenCookieName]);
            if (!authorized)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("認証が必要です（--token で指定したトークンを提示してください）。");
                return;
            }

            if (queryToken is not null)
            {
                context.Response.Cookies.Append(ServeAuthenticator.TokenCookieName, queryToken,
                    new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict });
            }

            await next();
        });
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/meta", () => Results.Json(new { repositoryPath = _repositoryPath }));
        app.MapGet("/api/resources", GetResources);
        app.MapGet("/api/config", GetConfig);
        app.MapGet("/api/history", GetHistoryAsync);
        app.MapGet("/api/history/{commit}", GetHistoryDetailAsync);
        app.MapGet("/api/logs/{commit}/{jobId}", GetJobLogAsync);
        app.MapPost("/api/config", SaveConfigAsync);
        app.MapPost("/api/run", StartRun);
        app.MapGet("/api/run", GetRunStatus);
        app.Map("/api/run/ws", StreamRunOverWebSocketAsync);
        app.MapGet("/api/plugins", GetPlugins);
        app.MapPost("/api/plugins", AddPluginAsync);
        app.MapPost("/api/plugins/remove", RemovePluginAsync);
        app.MapGet("/api/jobs", GetJobs);
        app.MapPost("/api/jobs", SaveJobAsync);
        app.MapPost("/api/jobs/remove", RemoveJobAsync);
        app.MapGet("/api/templates", () => Results.Json(new { templates = JobTemplateCatalog.Templates }));
        app.MapPost("/api/templates/apply", ApplyTemplateAsync);
    }

    /// <summary>ビルドテンプレートからジョブを生成して設定に追加し、--validate まで行う。</summary>
    private async Task<IResult> ApplyTemplateAsync(TemplateApplyPayload payload, CancellationToken cancellationToken)
    {
        Dictionary<string, object> values = payload.Values?
            .ToDictionary(pair => pair.Key, pair => ConvertTemplateValue(pair.Value)) ?? new();
        try
        {
            (string jobName, Dictionary<string, object> job) = JobTemplateCatalog.Instantiate(payload.Id, values);
            if (!System.Text.RegularExpressions.Regex.IsMatch(jobName, "^[A-Za-z0-9_.-]+$"))
            {
                return Results.Json(new { valid = false, output = "ジョブ名に使えるのは英数字と . - _ だけです。" });
            }

            string yaml = _configEditor.Read();
            if (JobConfigEditor.Read(yaml).Jobs.Any(existing => existing.Name == jobName))
            {
                return Results.Json(new
                {
                    valid = false, output = $"ジョブ「{jobName}」は既にあります。別のジョブ名を指定してください。"
                });
            }

            await _configEditor.WriteAsync(JobConfigEditor.UpsertMap(yaml, jobName, job), cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidPipelineException or YamlDotNet.Core.YamlException)
        {
            return Results.Json(new { valid = false, output = exception.Message });
        }

        ValidationResult validation = await _configEditor.ValidateAsync(cancellationToken);
        return Results.Json(new { valid = validation.IsValid, output = validation.Output });
    }

    /// <summary>テンプレート入力のJSON値（文字列または文字列配列）をカタログが扱う型へ変換する。</summary>
    private static object ConvertTemplateValue(System.Text.Json.JsonElement element)
        => element.ValueKind == System.Text.Json.JsonValueKind.Array
            ? element.EnumerateArray().Select(item => item.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : (object)(element.GetString() ?? "");

    private sealed record TemplateApplyPayload(
        string Id, Dictionary<string, System.Text.Json.JsonElement>? Values);

    /// <summary>フォーム編集用にジョブ一覧とステージ一覧を返す。YAMLが壊れていてもエラー内容ごと返す。</summary>
    private IResult GetJobs()
    {
        try
        {
            JobsSnapshot snapshot = JobConfigEditor.Read(_configEditor.Read());
            return Results.Json(new { stages = snapshot.Stages, jobs = snapshot.Jobs });
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            return Results.Json(new
            {
                stages = Array.Empty<string>(), jobs = Array.Empty<JobSummary>(), error = exception.Message
            });
        }
    }

    /// <summary>フォームからのジョブ追加・更新。設定に反映 → 保存 → --validate まで行う。</summary>
    private async Task<IResult> SaveJobAsync(JobFormPayload payload, CancellationToken cancellationToken)
    {
        string? inputError = ValidateJobInput(payload);
        if (inputError is not null) return Results.Json(new { valid = false, output = inputError });

        try
        {
            await _configEditor.WriteAsync(JobConfigEditor.Upsert(_configEditor.Read(), payload), cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidPipelineException or YamlDotNet.Core.YamlException)
        {
            return Results.Json(new { valid = false, output = exception.Message });
        }

        ValidationResult validation = await _configEditor.ValidateAsync(cancellationToken);
        return Results.Json(new { valid = validation.IsValid, output = validation.Output });
    }

    private async Task<IResult> RemoveJobAsync(JobRemovePayload payload, CancellationToken cancellationToken)
    {
        try
        {
            await _configEditor.WriteAsync(
                JobConfigEditor.Remove(_configEditor.Read(), payload.Name), cancellationToken);
        }
        catch (InvalidPipelineException exception)
        {
            return Results.Json(new { valid = false, output = exception.Message });
        }

        ValidationResult validation = await _configEditor.ValidateAsync(cancellationToken);
        return Results.Json(new { valid = validation.IsValid, output = validation.Output });
    }

    private static string? ValidateJobInput(JobFormPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Name))
        {
            return "ジョブ名を入力してください。";
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(payload.Name, "^[A-Za-z0-9_.-]+$"))
        {
            return "ジョブ名に使えるのは英数字と . - _ だけです。";
        }

        if (payload.Script is not { Count: > 0 } || payload.Script.All(string.IsNullOrWhiteSpace))
        {
            return "実行するコマンド（script）を1行以上入力してください。";
        }

        return null;
    }

    private sealed record JobRemovePayload(string Name);

    /// <summary>設定から plugins セクションだけを寛容に読み出して返す（他のキーの不備には影響されない）。</summary>
    private IResult GetPlugins()
    {
        try
        {
            return Results.Json(new { plugins = ReadPluginList() });
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            return Results.Json(new { plugins = Array.Empty<PluginReference>(), error = exception.Message });
        }
    }

    private async Task<IResult> AddPluginAsync(PluginPayload payload, CancellationToken cancellationToken)
    {
        bool hasPackage = !string.IsNullOrWhiteSpace(payload.Package);
        bool hasPath = !string.IsNullOrWhiteSpace(payload.Path);
        if (hasPackage == hasPath)
        {
            return Results.Json(new { valid = false, output = "package か path のどちらか一方を指定してください。" });
        }

        return await MutatePluginsAsync(
            yaml => hasPackage
                ? PluginConfigEditor.AddPackage(yaml, payload.Package!, payload.Version ?? "")
                : PluginConfigEditor.AddPath(yaml, payload.Path!),
            cancellationToken);
    }

    private async Task<IResult> RemovePluginAsync(PluginRemovePayload payload, CancellationToken cancellationToken)
        => await MutatePluginsAsync(
            yaml => PluginConfigEditor.Remove(yaml, payload.Identifier), cancellationToken);

    /// <summary>設定を書き換え → 保存 → --validate（プラグイン読み込み確認込み）まで行い、結果を返す。</summary>
    private async Task<IResult> MutatePluginsAsync(
        Func<string, string> mutate, CancellationToken cancellationToken)
    {
        try
        {
            await _configEditor.WriteAsync(mutate(_configEditor.Read()), cancellationToken);
        }
        catch (PluginException exception)
        {
            return Results.Json(new { valid = false, output = exception.Message });
        }

        ValidationResult validation = await _configEditor.ValidateAsync(cancellationToken);
        return Results.Json(new { valid = validation.IsValid, output = validation.Output, plugins = ReadPluginList() });
    }

    private IReadOnlyList<PluginReference> ReadPluginList()
    {
        YamlDotNet.Serialization.IDeserializer deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        PluginsOnly? parsed = deserializer.Deserialize<PluginsOnly?>(_configEditor.Read());
        return parsed?.Plugins ?? [];
    }

    /// <summary>plugins セクションだけを取り出すための寛容な読み取り用DTO。</summary>
    private sealed class PluginsOnly
    {
        public List<PluginReference> Plugins { get; set; } = new();
    }

    private sealed record PluginPayload(string? Package, string? Version, string? Path);
    private sealed record PluginRemovePayload(string Identifier);

    /// <summary>実行ログを WebSocket で逐次配信する。新しい出力行を push し、完了で done を送って閉じる。</summary>
    private async Task StreamRunOverWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        await PumpRunAsync(socket, context.RequestAborted);
    }

    private async Task PumpRunAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken)
    {
        int sentLineCount = 0;
        while (socket.State == System.Net.WebSockets.WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            RunStatus status = _runManager.GetStatus();
            for (; sentLineCount < status.OutputLines.Count; sentLineCount++)
            {
                await SendJsonAsync(socket, new { line = status.OutputLines[sentLineCount] }, cancellationToken);
            }

            if (!status.IsRunning && sentLineCount >= status.OutputLines.Count)
            {
                await SendJsonAsync(socket, new { done = true, exitCode = status.ExitCode }, cancellationToken);
                return;
            }

            await Task.Delay(300, cancellationToken);
        }
    }

    private static async Task SendJsonAsync(
        System.Net.WebSockets.WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private async Task<IResult> SaveConfigAsync(ConfigPayload payload, CancellationToken cancellationToken)
    {
        await _configEditor.WriteAsync(payload.Content, cancellationToken);
        ValidationResult validation = await _configEditor.ValidateAsync(cancellationToken);
        return Results.Json(new { valid = validation.IsValid, output = validation.Output });
    }

    private IResult StartRun(RunPayload? payload)
    {
        bool started = _runManager.TryStart(payload?.Rebuild ?? false, payload?.Job);
        return started
            ? Results.Json(new { started = true })
            : Results.Json(new { started = false, reason = "すでに実行中です。" });
    }

    private IResult GetRunStatus()
    {
        RunStatus status = _runManager.GetStatus();
        return Results.Json(new { running = status.IsRunning, exitCode = status.ExitCode, lines = status.OutputLines });
    }

    private sealed record ConfigPayload(string Content);
    private sealed record RunPayload(bool Rebuild, string? Job);

    private IResult GetResources()
    {
        SystemResourceSnapshot snapshot = new SystemResourceMonitor(new()).Capture(_repositoryPath);
        return Results.Json(snapshot);
    }

    /// <summary>設定ファイルがまだ無い場合は雛形を返し、エディタ上で新規作成できるようにする。</summary>
    private IResult GetConfig()
    {
        bool exists = _configEditor.Exists;
        string content = exists ? _configEditor.Read() : ConfigEditor.StarterTemplate;
        return Results.Json(new { fileName = _options.ConfigFileName, content, exists });
    }

    private async Task<IResult> GetHistoryAsync(CancellationToken cancellationToken)
    {
        HistoryManager historyManager = new(_dataRootPath);
        return Results.Json(await historyManager.ReadAllRecordsAsync(cancellationToken));
    }

    private async Task<IResult> GetHistoryDetailAsync(string commit, CancellationToken cancellationToken)
    {
        HistoryManager historyManager = new(_dataRootPath);
        BuildRecord? record = await historyManager.GetRecordAsync(commit, cancellationToken);
        return record is null ? Results.NotFound() : Results.Json(record);
    }

    private async Task<IResult> GetJobLogAsync(string commit, string jobId, CancellationToken cancellationToken)
    {
        HistoryManager historyManager = new(_dataRootPath);
        string? log = await historyManager.ReadJobLogAsync(commit, jobId, cancellationToken);
        return log is null ? Results.NotFound() : Results.Text(log, "text/plain; charset=utf-8");
    }
}
