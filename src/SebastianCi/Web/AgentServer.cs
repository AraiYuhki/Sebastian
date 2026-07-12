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

    public async Task RunAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapGet("/agent/info", GetInfo);
        app.MapPost("/agent/run", RunJobAsync);

        string url = $"http://localhost:{_options.Port}";
        ConsoleLogger.WriteSuccess($"🛰 エージェントを起動しました: {url} (エンジン: {_engine.ExecutableName})");
        ConsoleLogger.WriteInfo($"   ジョブに `agent: {url}` を指定するとこのエージェントで実行されます。Ctrl+C で停止。");
        await app.RunAsync(url);
    }

    private IResult GetInfo()
    {
        SystemResourceSnapshot resources = new SystemResourceMonitor(new()).Capture(_repositoryPath);
        return Results.Json(new { engine = _engine.ExecutableName, workspace = _repositoryPath, resources });
    }

    private async Task<IResult> RunJobAsync(AgentJobRequest request, CancellationToken cancellationToken)
    {
        ConsoleLogger.WriteInfo($"📥 ジョブ '{request.JobId}' を受信しました。実行します。");
        string logDirectoryPath = Path.Combine(
            Path.GetTempPath(), "sebastian-ci-agent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectoryPath);

        int exitCode = await ExecuteAsync(request, logDirectoryPath, cancellationToken);
        List<string> log = ReadLog(logDirectoryPath, request.JobId);
        return Results.Json(new AgentJobResponse(exitCode, log));
    }

    private async Task<int> ExecuteAsync(
        AgentJobRequest request, string logDirectoryPath, CancellationToken cancellationToken)
    {
        JobDefinition job = new()
        {
            Image = request.Image,
            Script = request.Script,
            Env = request.Env,
            Timeout = request.Timeout,
            Cache = request.Cache
        };
        ContainerRunner runner = new(_engine, new ActiveContainerRegistry(), _repositoryPath, logDirectoryPath, _cacheRootPath);

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

    private static List<string> ReadLog(string logDirectoryPath, string jobId)
    {
        string logPath = Path.Combine(logDirectoryPath, $"{PathSanitizer.ToFileSystemName(jobId)}.log");
        return File.Exists(logPath) ? File.ReadAllLines(logPath).ToList() : new List<string>();
    }
}
