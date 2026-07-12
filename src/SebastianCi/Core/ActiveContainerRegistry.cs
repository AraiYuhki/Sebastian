namespace SebastianCi.Core;

/// <summary>
/// 実行中コンテナの中央追跡だけを担当する。
/// 並列ジョブから同時に登録・解除されるためスレッドセーフに実装する。
/// </summary>
public sealed class ActiveContainerRegistry
{
    private readonly object _syncRoot = new();
    private readonly HashSet<ActiveContainer> _activeContainers = new();

    public void Register(ActiveContainer container)
    {
        lock (_syncRoot)
        {
            _activeContainers.Add(container);
        }
    }

    public void Unregister(ActiveContainer container)
    {
        lock (_syncRoot)
        {
            _activeContainers.Remove(container);
        }
    }

    /// <summary>現時点で追跡中のコンテナ一覧のコピーを返す（クリーンアップ用）。</summary>
    public IReadOnlyList<ActiveContainer> Snapshot()
    {
        lock (_syncRoot)
        {
            return _activeContainers.ToList();
        }
    }
}
