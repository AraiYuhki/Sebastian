namespace SebastianCi.Core;

/// <summary>
/// approval 指定ジョブの実行可否を人間（または自動承認）に問い合わせる拡張点。
/// </summary>
public interface IApprovalGate
{
    /// <summary>承認されれば true、拒否（または応答不能）なら false を返す。</summary>
    Task<bool> RequestAsync(string jobId, string message, CancellationToken cancellationToken = default);
}
