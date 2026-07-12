using System.Net.Http.Json;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの実行をリモートエージェント（sebastian-ci agent）へ HTTP で委譲する実行部。
/// エージェントが返した出力をローカルのコンソールにも表示し、失敗時は例外をスローする。
/// </summary>
public sealed class RemoteAgentRunner : IJobRunner
{
    private readonly HttpClient _httpClient;

    public RemoteAgentRunner(HttpClient httpClient) => _httpClient = httpClient;

    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        ConsoleLogger.WriteInfo($"📡 ジョブ '{jobId}' をエージェント {job.Agent} に委譲します");
        AgentJobResponse response = await SendAsync(jobId, job, cancellationToken);

        foreach (string line in response.Log)
        {
            ConsoleLogger.WriteJobOutput(jobId, line, isError: false);
        }

        if (response.ExitCode != 0)
        {
            throw new ContainerExecutionException(
                $"エージェント {job.Agent} 上でジョブ '{jobId}' が終了コード {response.ExitCode} で失敗しました。");
        }
    }

    private async Task<AgentJobResponse> SendAsync(
        string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        AgentJobRequest request = new(jobId, job.Image, job.Script, job.Env, job.Timeout, job.Cache);
        string url = $"{job.Agent.TrimEnd('/')}/agent/run";

        try
        {
            using HttpRequestMessage message = new(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(request)
            };
            if (!string.IsNullOrEmpty(job.AgentToken))
            {
                message.Headers.Add("X-Agent-Token", job.AgentToken);
            }

            using HttpResponseMessage http = await _httpClient.SendAsync(message, cancellationToken);
            if (!http.IsSuccessStatusCode)
            {
                throw new ContainerExecutionException(
                    $"エージェント {job.Agent} が HTTP {(int)http.StatusCode} を返しました (ジョブ: {jobId})。");
            }

            return await http.Content.ReadFromJsonAsync<AgentJobResponse>(cancellationToken)
                ?? throw new ContainerExecutionException($"エージェント {job.Agent} の応答が空でした (ジョブ: {jobId})。");
        }
        catch (HttpRequestException exception)
        {
            throw new ContainerExecutionException(
                $"エージェント {job.Agent} に接続できません (ジョブ: {jobId}): {exception.Message}");
        }
    }
}
