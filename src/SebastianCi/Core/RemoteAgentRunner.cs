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
    private readonly string? _workspaceTarBase64;

    public RemoteAgentRunner(HttpClient httpClient, byte[]? workspaceArchive = null)
    {
        _httpClient = httpClient;
        // 転送量を減らすため gzip 圧縮してから base64 化する（エージェント側で展開）。
        _workspaceTarBase64 = workspaceArchive is null
            ? null
            : Convert.ToBase64String(ArchiveCodec.Compress(workspaceArchive));
    }

    /// <summary>指定エージェントでジョブを実行する。失敗時は ContainerExecutionException をスローする。</summary>
    public async Task RunOnAsync(
        AgentEndpoint endpoint, string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        int? exitCode = await StreamAsync(endpoint, jobId, job, cancellationToken);
        if (exitCode is not 0)
        {
            throw new ContainerExecutionException(
                $"エージェント {endpoint.Url} 上でジョブ '{jobId}' が終了コード {exitCode} で失敗しました。");
        }
    }

    /// <summary>エージェントの NDJSON ストリームを読み、出力行を逐次表示しつつ終了コードを取得する。</summary>
    private async Task<int?> StreamAsync(
        AgentEndpoint endpoint, string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage http = await SendRequestAsync(endpoint, jobId, job, cancellationToken);
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
        AgentEndpoint endpoint, string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        AgentJobRequest request = new(
            jobId, job.Image, job.Script, job.Env, job.Timeout, job.Cache, _workspaceTarBase64);
        using HttpRequestMessage message = new(HttpMethod.Post, $"{endpoint.Url.TrimEnd('/')}/agent/run")
        {
            Content = JsonContent.Create(request)
        };
        if (!string.IsNullOrEmpty(endpoint.Token))
        {
            message.Headers.Add("X-Agent-Token", endpoint.Token);
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
