using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Web;

/// <summary>
/// SlaveAgent：他マシン（またはローカル）でジョブ実行を肩代わりするエージェントサーバー。
/// マスターから受け取ったジョブを、このエージェントのワークスペースでコンテナ実行
/// （shell ジョブはホスト直接実行）して結果を返す。コンテナエンジンが見つからない環境でも
/// shell ジョブ専用エージェントとして起動できる（macOS の Xcode ビルド等を想定）。
/// </summary>
public sealed class AgentServer
{
    private readonly AgentOptions _options;
    private readonly string _repositoryPath;
    private readonly ContainerEngine? _engine;
    private readonly string _cacheRootPath;
    private readonly AgentWorkspaceCache _workspaceCache;

    private AgentServer(AgentOptions options, string repositoryPath, ContainerEngine? engine)
    {
        _options = options;
        _repositoryPath = repositoryPath;
        _engine = engine;
        _cacheRootPath = Path.Combine(repositoryPath, HistoryManager.DefaultDataDirectoryName, "agent-cache");
        _workspaceCache = new AgentWorkspaceCache(
            Path.Combine(Path.GetTempPath(), "sebastian-ci-agent", "workspaces", options.Port.ToString()));
    }

    /// <summary>
    /// エンジンを解決してエージェントを生成する。--engine 未指定でエンジンが見つからない場合は
    /// 起動を失敗させず、shell ジョブ専用のエージェントとして生成する。
    /// </summary>
    public static async Task<AgentServer> CreateAsync(AgentOptions options, CancellationToken cancellationToken = default)
    {
        string repositoryPath = Path.GetFullPath(options.RepositoryPath);
        ContainerEngine? engine = options.EngineName is null
            ? await TryDetectEngineAsync(cancellationToken)
            : ContainerEngine.FromName(options.EngineName)
                ?? throw new ContainerExecutionException($"未対応のコンテナエンジンです: {options.EngineName}");
        return new AgentServer(options, repositoryPath, engine);
    }

    private static async Task<ContainerEngine?> TryDetectEngineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ContainerEngine.DetectAsync(cancellationToken);
        }
        catch (ContainerExecutionException)
        {
            return null;
        }
    }

    public const string TokenHeaderName = "X-Agent-Token";

    public async Task RunAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.Use(RequireTokenAsync);
        app.MapGet("/agent/info", GetInfo);
        app.MapGet("/agent/commits", () => Results.Json(new AgentCommitsResponse(_workspaceCache.ListCommits().ToList())));
        app.MapPost("/agent/run", RunJobStreamingAsync);

        string url = $"http://localhost:{_options.Port}";
        string engineLabel = _engine?.ExecutableName ?? "なし（shell ジョブのみ実行可能）";
        ConsoleLogger.WriteSuccess($"🛰 エージェントを起動しました: {url} (エンジン: {engineLabel})");
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
        return Results.Json(new { engine = _engine?.ExecutableName ?? "none", workspace = _repositoryPath, resources });
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

    /// <summary>
    /// マスターから送られたワークスペースを一時ディレクトリに用意して返す。
    /// 差分転送（BaseCommitHash 指定）ならキャッシュ済みベースに変更分を適用し、
    /// そうでなければ全体を展開する。用意したものはコミット単位でキャッシュする。
    /// </summary>
    private async Task<string?> MaterializeWorkspaceAsync(
        AgentJobRequest request, CancellationToken cancellationToken)
    {
        if (request.WorkspaceTarBase64 is null) return null;

        string workspacePath = CreateTempDirectory("workspace");
        SeedFromBase(request, workspacePath);
        await ExtractOverlayAsync(request.WorkspaceTarBase64, workspacePath, cancellationToken);
        ApplyDeletions(request.DeletedPaths, workspacePath);

        if (request.CommitHash is not null) _workspaceCache.Store(request.CommitHash, workspacePath);
        ConsoleLogger.WriteInfo($"📦 ワークスペースを用意しました: {workspacePath}");
        return workspacePath;
    }

    private void SeedFromBase(AgentJobRequest request, string workspacePath)
    {
        if (request.BaseCommitHash is null) return;

        if (!_workspaceCache.TryCopyInto(request.BaseCommitHash, workspacePath))
        {
            throw new ContainerExecutionException(
                $"差分転送のベース {request.BaseCommitHash} がエージェントのキャッシュに見つかりません。");
        }
    }

    private static async Task ExtractOverlayAsync(
        string tarGzBase64, string workspacePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tarGzBase64)) return;

        using MemoryStream compressed = new(Convert.FromBase64String(tarGzBase64));
        byte[] tar = await ArchiveCodec.DecompressAsync(compressed, cancellationToken);
        using MemoryStream tarStream = new(tar);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(
            tarStream, workspacePath, overwriteFiles: true, cancellationToken);
    }

    private static void ApplyDeletions(List<string>? deletedPaths, string workspacePath)
    {
        if (deletedPaths is null) return;

        foreach (string relativePath in deletedPaths)
        {
            string fullPath = Path.GetFullPath(Path.Combine(workspacePath, relativePath));
            if (fullPath.StartsWith(workspacePath, StringComparison.Ordinal) && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
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
            Cache = request.Cache,
            Shell = request.Shell
        };

        try
        {
            IJobRunner runner = ResolveRunner(request, workspacePath, logDirectoryPath, outputObserver);
            await runner.RunJobAsync(request.JobId, job, cancellationToken);
            return 0;
        }
        catch (ContainerExecutionException exception)
        {
            ConsoleLogger.WriteError($"❌ ジョブ '{request.JobId}' が失敗しました: {exception.Message}");
            return 1;
        }
    }

    /// <summary>shell ジョブはホスト直接実行、それ以外はコンテナ実行。エンジンの無い環境ではコンテナ実行を拒否する。</summary>
    private IJobRunner ResolveRunner(
        AgentJobRequest request, string workspacePath, string logDirectoryPath, Action<string, bool> outputObserver)
    {
        if (request.Shell) return new ShellRunner(workspacePath, logDirectoryPath, outputObserver);

        if (_engine is null)
        {
            throw new ContainerExecutionException(
                "このエージェントにはコンテナエンジン（podman / docker）が無いため、shell 以外のジョブは実行できません。");
        }

        return new ContainerRunner(
            _engine, new ActiveContainerRegistry(), workspacePath, logDirectoryPath, _cacheRootPath, outputObserver);
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
