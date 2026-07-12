namespace SebastianCi.Core;

/// <summary>
/// エージェントへ送るワークスペースのペイロード生成だけを担当する。
/// エージェントが既に持っているコミットがあれば差分（変更ファイルのみ）を、無ければ全体を返す。
/// gzip 圧縮した tar を base64 化して返す。全体アーカイブは一度だけ作って再利用する。
/// </summary>
public sealed class WorkspaceSource
{
    private readonly GitManager _gitManager;
    private readonly string _commitHash;
    private readonly SemaphoreSlim _fullArchiveLock = new(1, 1);
    private string? _fullArchiveBase64;

    public WorkspaceSource(GitManager gitManager, string commitHash)
    {
        _gitManager = gitManager;
        _commitHash = commitHash;
    }

    /// <summary>エージェントが持つコミット一覧を踏まえ、送るべきペイロードを構築する。</summary>
    public async Task<WorkspacePayload> BuildForAsync(
        IReadOnlyList<string> agentCommits, CancellationToken cancellationToken = default)
    {
        // エージェントが対象コミットを既に持っていれば、何も送らずキャッシュを再利用させる。
        if (agentCommits.Contains(_commitHash))
        {
            return new WorkspacePayload(_commitHash, "", _commitHash, []);
        }

        string? baseCommit = agentCommits.FirstOrDefault(commit => commit != _commitHash);
        return baseCommit is null
            ? new WorkspacePayload(_commitHash, await GetFullArchiveAsync(cancellationToken), null, [])
            : await BuildDeltaAsync(baseCommit, cancellationToken);
    }

    private async Task<WorkspacePayload> BuildDeltaAsync(string baseCommit, CancellationToken cancellationToken)
    {
        (IReadOnlyList<string> changed, IReadOnlyList<string> deleted) =
            await _gitManager.GetDiffStatusAsync(baseCommit, _commitHash, cancellationToken);
        byte[] tar = await _gitManager.CreateArchiveAsync(_commitHash, changed, cancellationToken);
        string base64 = Convert.ToBase64String(ArchiveCodec.Compress(tar));
        return new WorkspacePayload(_commitHash, base64, baseCommit, deleted.ToList());
    }

    private async Task<string> GetFullArchiveAsync(CancellationToken cancellationToken)
    {
        await _fullArchiveLock.WaitAsync(cancellationToken);
        try
        {
            if (_fullArchiveBase64 is not null) return _fullArchiveBase64;

            byte[] tar = await _gitManager.CreateArchiveAsync(_commitHash, null, cancellationToken);
            return _fullArchiveBase64 = Convert.ToBase64String(ArchiveCodec.Compress(tar));
        }
        finally
        {
            _fullArchiveLock.Release();
        }
    }
}

/// <summary>エージェントへ送るワークスペースのペイロード（全体 or 差分）。</summary>
public sealed record WorkspacePayload(
    string CommitHash, string TarGzBase64, string? BaseCommitHash, List<string> DeletedPaths);
