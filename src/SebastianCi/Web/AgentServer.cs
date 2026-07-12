using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Web;

/// <summary>
/// SlaveAgent：他マシン（またはローカル）でコンテナ実行を肩代わりするエージェントサーバー。
/// マスターから受け取ったジョブを、このエージェントのワークスペースでコンテナ実行して結果を返す。
/// </summary>
public sealed class AgentServer
{
    private readonly AgentOptions _options;
    private readonly string _repositoryPath;
    private readonly ContainerEngine _engine;
    private readonly string _cacheRootPath;

    private AgentServer(AgentOptions options, string repositoryPath, ContainerEngine engine)
    {
        _options = options;
        _repositoryPath = repositoryPath;
        _engine = engine;
        _cacheRootPath = Path.Combine(repositoryPath, HistoryManager.DefaultDataDirectoryName, "agent-cache");
    }

    /// <summary>エンジンを解決してエージェントを生成する。</summary>
    public static async Task<AgentServer> CreateAsync(AgentOptions options, CancellationToken cancellationToken = default)
    {
        string repositoryPath = Path.GetFullPath(options.RepositoryPath);
        ContainerEngine engine = options.EngineName is null
            ? await ContainerEngine.DetectAsync(cancellationToken)
            : ContainerEngine.FromName(options.EngineName)
                ?? throw new ContainerExecutionException($"未対応のコンテナエンジンです: {options.EngineName}");
        return new AgentServer(options, repositoryPath, engine);
    }

    public const string TokenHeaderName = "X-Agent-Token";

    public async Task RunAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.Use(RequireTokenAsync);
        app.MapGet("/agent/info", GetInfo);
        app.MapPost("/agent/run", RunJobStreamingAsync);

        string url = $"http://localhost:{_options.Port}";
        ConsoleLogger.WriteSuccess($"🛰 エージェントを起動しました: {url} (エンジン: {_engine.ExecutableName})");
        ConsoleLogger.WriteInfo(_options.Token is null
            ? "   ⚠ 認証トークン未設定（誰でも実行できます）。--token で設定を推奨します。Ctrl+C で停止。"
            : "   認証トークンが必要です。ジョブに agentToken を指定してください。Ctrl+C で停止。");
        await app.RunAsync(url);
    }

    /// <summary>トークンが設定されている場合、一致する X-Agent-Token ヘッダーを要求する。</summary>
    private async Task RequireTokenAsync(HttpContext context, Func<Task> next)
    {
        if (_options.Token is null || context.Request.Headers[TokenHeaderName] == _options.Token)
        {
            await next();
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("認証トークンが正しくありません。");
    }

    private IResult GetInfo()
    {
        SystemResourceSnapshot resources = new SystemResourceMonitor(new()).Capture(_repositoryPath);
        return Results.Json(new { engine = _engine.ExecutableName, workspace = _repositoryPath, resources });
    }

    /// <summary>
    /// ジョブを実行し、出力を NDJSON（1行1メッセージ）で逐次ストリーミングする。
    /// マスターはこれを読みながらリアルタイムにログを表示できる。
    /// </summary>
    private async Task RunJobStreamingAsync(HttpContext context)
    {
        AgentJobRequest? request = await context.Request.ReadFromJsonAsync<AgentJobRequest>(context.RequestAborted);
        if (request is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        ConsoleLogger.WriteInfo($"📥 ジョブ '{request.JobId}' を受信しました。実行します。");
        context.Response.ContentType = "application/x-ndjson";
        await StreamExecutionAsync(request, context);
    }

    private async Task StreamExecutionAsync(AgentJobRequest request, HttpContext context)
    {
        Channel<AgentStreamMessage> channel = Channel.CreateUnbounded<AgentStreamMessage>();
        Task runTask = RunAndPublishAsync(request, channel, context.RequestAborted);

        await foreach (AgentStreamMessage message in channel.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(message) + "\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        await runTask;
    }

    private async Task RunAndPublishAsync(
        AgentJobRequest request, Channel<AgentStreamMessage> channel, CancellationToken cancellationToken)
    {
        string logDirectoryPath = CreateTempDirectory("logs");
        string? syncedWorkspace = await MaterializeWorkspaceAsync(request, cancellationToken);
        void Observe(string line, bool _) => channel.Writer.TryWrite(new AgentStreamMessage(Line: line));

        try
        {
            int exitCode = await ExecuteAsync(
                request, syncedWorkspace ?? _repositoryPath, logDirectoryPath, Observe, cancellationToken);
            channel.Writer.TryWrite(new AgentStreamMessage(ExitCode: exitCode));
        }
        finally
        {
            channel.Writer.Complete();
            if (syncedWorkspace is not null) TryDeleteDirectory(syncedWorkspace);
        }
    }

    /// <summary>マスターから送られたソースアーカイブがあれば一時ディレクトリへ展開し、そのパスを返す。</summary>
    private static async Task<string?> MaterializeWorkspaceAsync(
        AgentJobRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.WorkspaceTarBase64)) return null;

        string workspacePath = CreateTempDirectory("workspace");
        using MemoryStream compressed = new(Convert.FromBase64String(request.WorkspaceTarBase64));
        byte[] tar = await ArchiveCodec.DecompressAsync(compressed, cancellationToken);
        using MemoryStream tarStream = new(tar);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(
            tarStream, workspacePath, overwriteFiles: true, cancellationToken);
        ConsoleLogger.WriteInfo($"📦 マスターのソースを展開しました: {workspacePath}");
        return workspacePath;
    }

    private async Task<int> ExecuteAsync(
        AgentJobRequest request, string workspacePath, string logDirectoryPath,
        Action<string, bool> outputObserver, CancellationToken cancellationToken)
    {
        JobDefinition job = new()
        {
            Image = request.Image,
            Script = request.Script,
            Env = request.Env,
            Timeout = request.Timeout,
            Cache = request.Cache
        };
        ContainerRunner runner = new(
            _engine, new ActiveContainerRegistry(), workspacePath, logDirectoryPath, _cacheRootPath, outputObserver);

        try
        {
            await runner.RunJobAsync(request.JobId, job, cancellationToken);
            return 0;
        }
        catch (ContainerExecutionException exception)
        {
            ConsoleLogger.WriteError($"❌ ジョブ '{request.JobId}' が失敗しました: {exception.Message}");
            return 1;
        }
    }

    private static string CreateTempDirectory(string kind)
    {
        string path = Path.Combine(Path.GetTempPath(), "sebastian-ci-agent", kind, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException exception)
        {
            ConsoleLogger.WriteWarning($"⚠ 一時ワークスペースを削除できませんでした: {exception.Message}");
        }
    }
}
