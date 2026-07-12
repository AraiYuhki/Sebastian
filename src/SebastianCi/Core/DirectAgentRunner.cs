using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブに明示指定された単一エージェント（agent: URL）へ実行を委譲する IJobRunner。
/// </summary>
public sealed class DirectAgentRunner : IJobRunner
{
    private readonly RemoteAgentRunner _sender;

    public DirectAgentRunner(RemoteAgentRunner sender) => _sender = sender;

    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        ConsoleLogger.WriteInfo($"📡 ジョブ '{jobId}' をエージェント {job.Agent} に委譲します");
        AgentEndpoint endpoint = new() { Url = job.Agent, Token = job.AgentToken };
        await _sender.RunOnAsync(endpoint, jobId, job, cancellationToken);
    }
}
