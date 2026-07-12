using System.Net.Http.Json;
using System.Text.Json;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの実行をリモートエージェント（sebastian-ci agent）へ HTTP で委譲する実行部。
/// エージェントの出力を NDJSON ストリームで受け取り、逐次コンソールに表示する。失敗時は例外をスローする。
/// </summary>
public sealed class RemoteAgentRunner : IJobRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string? _workspaceTarBase64;

    public RemoteAgentRunner(HttpClient httpClient, byte[]? workspaceArchive = null)
    {
        _httpClient = httpClient;
        _workspaceTarBase64 = workspaceArchive is null ? null : Convert.ToBase64String(workspaceArchive);
    }

    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        ConsoleLogger.WriteInfo($"📡 ジョブ '{jobId}' をエージェント {job.Agent} に委譲します");
        int? exitCode = await StreamAsync(jobId, job, cancellationToken);

        if (exitCode is not 0)
        {
            throw new ContainerExecutionException(
                $"エージェント {job.Agent} 上でジョブ '{jobId}' が終了コード {exitCode} で失敗しました。");
        }
    }

    /// <summary>エージェントの NDJSON ストリームを読み、出力行を逐次表示しつつ終了コードを取得する。</summary>
    private async Task<int?> StreamAsync(string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage http = await SendRequestAsync(jobId, job, cancellationToken);
            if (!http.IsSuccessStatusCode)
            {
                throw new ContainerExecutionException(
                    $"エージェント {job.Agent} が HTTP {(int)http.StatusCode} を返しました (ジョブ: {jobId})。");
            }

            return await ReadStreamAsync(jobId, http, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ContainerExecutionException(
                $"エージェント {job.Agent} に接続できません (ジョブ: {jobId}): {exception.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        AgentJobRequest request = new(
            jobId, job.Image, job.Script, job.Env, job.Timeout, job.Cache, _workspaceTarBase64);
        using HttpRequestMessage message = new(HttpMethod.Post, $"{job.Agent.TrimEnd('/')}/agent/run")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrEmpty(job.AgentToken))
        {
            message.Headers.Add("X-Agent-Token", job.AgentToken);
        }

        return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<int?> ReadStreamAsync(
        string jobId, HttpResponseMessage http, CancellationToken cancellationToken)
    {
        await using Stream stream = await http.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream);
        int? exitCode = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } jsonLine)
        {
            if (string.IsNullOrWhiteSpace(jsonLine)) continue;

            AgentStreamMessage? message = JsonSerializer.Deserialize<AgentStreamMessage>(jsonLine, JsonOptions);
            if (message?.Line is not null) ConsoleLogger.WriteJobOutput(jobId, message.Line, isError: false);
            if (message?.ExitCode is not null) exitCode = message.ExitCode;
        }

        return exitCode;
    }
}
