using System.Net.Http.Json;
using System.Text.Json;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 指定エンドポイントのエージェントへジョブ実行を HTTP 委譲する低レベル送信部。
/// エージェントの出力を NDJSON ストリームで受け取り、逐次コンソールに表示する。失敗時は例外をスローする。
/// </summary>
public sealed class RemoteAgentRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly WorkspaceSource? _workspaceSource;

    public RemoteAgentRunner(HttpClient httpClient, WorkspaceSource? workspaceSource = null)
    {
        _httpClient = httpClient;
        _workspaceSource = workspaceSource;
    }

    /// <summary>指定エージェントでジョブを実行する。失敗時は ContainerExecutionException をスローする。</summary>
    public async Task RunOnAsync(
        AgentEndpoint endpoint, string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        WorkspacePayload? payload = await BuildPayloadAsync(endpoint, cancellationToken);
        int? exitCode = await StreamAsync(endpoint, jobId, job, payload, cancellationToken);
        if (exitCode is not 0)
        {
            throw new ContainerExecutionException(
                $"エージェント {endpoint.Url} 上でジョブ '{jobId}' が終了コード {exitCode} で失敗しました。");
        }
    }

    /// <summary>エージェントのキャッシュ状況を問い合わせ、全体 or 差分のペイロードを組み立てる。</summary>
    private async Task<WorkspacePayload?> BuildPayloadAsync(AgentEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (_workspaceSource is null) return null;

        IReadOnlyList<string> agentCommits = await GetAgentCommitsAsync(endpoint, cancellationToken);
        WorkspacePayload payload = await _workspaceSource.BuildForAsync(agentCommits, cancellationToken);
        if (payload.BaseCommitHash is not null)
        {
            ConsoleLogger.WriteInfo($"🔻 差分転送: {endpoint.Url} は {payload.BaseCommitHash[..8]} を保持 → 変更分のみ送信");
        }

        return payload;
    }

    private async Task<IReadOnlyList<string>> GetAgentCommitsAsync(
        AgentEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage message = new(HttpMethod.Get, $"{endpoint.Url.TrimEnd('/')}/agent/commits");
            AddToken(message, endpoint);
            using HttpResponseMessage http = await _httpClient.SendAsync(message, cancellationToken);
            if (!http.IsSuccessStatusCode) return [];

            AgentCommitsResponse? response =
                await http.Content.ReadFromJsonAsync<AgentCommitsResponse>(JsonOptions, cancellationToken);
            return response?.Commits ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private async Task<int?> StreamAsync(
        AgentEndpoint endpoint, string jobId, JobDefinition job,
        WorkspacePayload? payload, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage http = await SendRequestAsync(endpoint, jobId, job, payload, cancellationToken);
            if (!http.IsSuccessStatusCode)
            {
                throw new ContainerExecutionException(
                    $"エージェント {endpoint.Url} が HTTP {(int)http.StatusCode} を返しました (ジョブ: {jobId})。");
            }

            return await ReadStreamAsync(jobId, http, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AgentUnreachableException(
                $"エージェント {endpoint.Url} に接続できません (ジョブ: {jobId}): {exception.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        AgentEndpoint endpoint, string jobId, JobDefinition job,
        WorkspacePayload? payload, CancellationToken cancellationToken)
    {
        AgentJobRequest request = new(
            jobId, job.Image, job.Script, job.Env, job.Timeout, job.Cache,
            payload?.TarGzBase64, payload?.CommitHash, payload?.BaseCommitHash, payload?.DeletedPaths,
            job.Shell, job.AfterScript);
        using HttpRequestMessage message = new(HttpMethod.Post, $"{endpoint.Url.TrimEnd('/')}/agent/run")
        {
            Content = JsonContent.Create(request)
        };
        AddToken(message, endpoint);
        return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static void AddToken(HttpRequestMessage message, AgentEndpoint endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.Token)) message.Headers.Add("X-Agent-Token", endpoint.Token);
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
