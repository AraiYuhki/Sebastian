using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 複数エージェントへの負荷分散（実行中ジョブ数が最も少ないエージェントを選ぶ）と、
/// 接続できないエージェントを避けるフェイルオーバーだけを担当する。スレッドセーフ。
/// </summary>
public sealed class AgentPool
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<AgentEndpoint, int> _activeCounts;

    public AgentPool(IReadOnlyList<AgentEndpoint> endpoints)
        => _activeCounts = endpoints.ToDictionary(endpoint => endpoint, _ => 0);

    public bool IsEmpty => _activeCounts.Count == 0;

    /// <summary>
    /// 実行中ジョブ数が最も少ないエージェントから順に、まだ試していないものを1件返す。
    /// すべて試し終えていれば null を返す。
    /// </summary>
    public AgentEndpoint? PickNext(IReadOnlyCollection<AgentEndpoint> alreadyTried)
    {
        lock (_syncRoot)
        {
            return _activeCounts
                .Where(pair => !alreadyTried.Contains(pair.Key))
                .OrderBy(pair => pair.Value)
                .Select(pair => pair.Key)
                .FirstOrDefault();
        }
    }

    public void Acquire(AgentEndpoint endpoint)
    {
        lock (_syncRoot)
        {
            _activeCounts[endpoint]++;
        }
    }

    public void Release(AgentEndpoint endpoint)
    {
        lock (_syncRoot)
        {
            if (_activeCounts.TryGetValue(endpoint, out int count) && count > 0)
            {
                _activeCounts[endpoint] = count - 1;
            }
        }
    }
}
