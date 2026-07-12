namespace SebastianCi.Core;

/// <summary>
/// プラグインが提供する独自のジョブ実行バックエンドを表す拡張点。
/// ジョブの <c>runner</c> に <see cref="Name"/> を指定すると、このランナーで実行される。
/// </summary>
public interface IPluginJobRunner
{
    /// <summary>ジョブの runner で指定される名前（例: kubernetes, ssh）。</summary>
    string Name { get; }

    /// <summary>ジョブを実行し、終了コードを返す（0 が成功）。出力は context 経由で通知する。</summary>
    Task<int> RunAsync(PluginJobContext context, CancellationToken cancellationToken = default);
}
