namespace SebastianCi.Core;

/// <summary>
/// 中断時に残った実行中コンテナの停止（stop -t 2）と削除（rm）だけを担当する。
/// </summary>
public sealed class ContainerCleanup
{
    private const int StopTimeoutSeconds = 2;

    private readonly ActiveContainerRegistry _registry;

    public ContainerCleanup(ActiveContainerRegistry registry) => _registry = registry;

    /// <summary>追跡中のすべてのコンテナへ停止・削除を並列（Task.WhenAll）で実行する。</summary>
    public async Task StopAndRemoveAllAsync()
    {
        IReadOnlyList<ActiveContainer> containers = _registry.Snapshot();
        if (containers.Count == 0) return;

        ConsoleLogger.WriteWarning($"🧹 実行中のコンテナ {containers.Count} 件を停止・削除しています...");
        await Task.WhenAll(containers.Select(StopAndRemoveAsync));
        ConsoleLogger.WriteWarning("🧹 コンテナのクリーンアップが完了しました。");
    }

    private async Task StopAndRemoveAsync(ActiveContainer container)
    {
        // --rm 付きで起動しているため stop 完了時点で自動削除されるのが通常だが、
        // 仕様どおり rm も発行して確実に削除する。既に消えている場合の失敗は無害なため終了コードは検証しない。
        await container.Engine.ExecuteQuietlyAsync(
            ["stop", "-t", StopTimeoutSeconds.ToString(), container.Name]);
        await container.Engine.ExecuteQuietlyAsync(["rm", container.Name]);
        _registry.Unregister(container);
    }
}
