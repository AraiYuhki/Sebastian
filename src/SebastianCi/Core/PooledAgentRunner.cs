using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// remote 指定ジョブを、エージェントプールの中で最も空いているエージェントへ割り当てて実行する IJobRunner。
/// 接続できないエージェントは避け、次に空いているエージェントへフェイルオーバーする。
/// </summary>
public sealed class PooledAgentRunner : IJobRunner
{
    private readonly AgentPool _pool;
    private readonly RemoteAgentRunner _sender;

    public PooledAgentRunner(AgentPool pool, RemoteAgentRunner sender)
    {
        _pool = pool;
        _sender = sender;
    }

    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        List<AgentEndpoint> tried = new();
        while (_pool.PickNext(tried) is { } endpoint)
        {
            tried.Add(endpoint);
            if (await TryRunOnAsync(endpoint, jobId, job, cancellationToken)) return;
        }

        throw new ContainerExecutionException(
            $"ジョブ '{jobId}' を実行できるエージェントがプールにありませんでした（{tried.Count} 件試行）。");
    }

    /// <summary>1エージェントで試す。接続できなければ false（次を試す）、それ以外の失敗は例外を伝播する。</summary>
    private async Task<bool> TryRunOnAsync(
        AgentEndpoint endpoint, string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        _pool.Acquire(endpoint);
        ConsoleLogger.WriteInfo($"📡 ジョブ '{jobId}' をプールのエージェント {endpoint.Url} に割り当てました");
        try
        {
            await _sender.RunOnAsync(endpoint, jobId, job, cancellationToken);
            return true;
        }
        catch (AgentUnreachableException exception)
        {
            ConsoleLogger.WriteWarning($"⚠ {exception.Message} 別のエージェントを試します。");
            return false;
        }
        finally
        {
            _pool.Release(endpoint);
        }
    }
}
