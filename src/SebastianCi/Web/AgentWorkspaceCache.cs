namespace SebastianCi.Web;

/// <summary>
/// エージェントがコミット単位でワークスペースを保持するキャッシュだけを担当する。
/// 差分転送のベースとして使い、古いものは上限を超えたら削除する。スレッドセーフ。
/// </summary>
public sealed class AgentWorkspaceCache
{
    private const int MaxCachedCommits = 10;

    private readonly string _root;
    private readonly object _syncRoot = new();

    public AgentWorkspaceCache(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    /// <summary>キャッシュ済みのコミットハッシュ一覧を返す。</summary>
    public IReadOnlyList<string> ListCommits()
    {
        lock (_syncRoot)
        {
            return Directory.GetDirectories(_root).Select(Path.GetFileName).OfType<string>().ToList();
        }
    }

    /// <summary>指定コミットのキャッシュディレクトリを返す（無ければ null）。</summary>
    public string? GetPath(string commitHash)
    {
        string path = Path.Combine(_root, commitHash);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>指定コミットのキャッシュを destination へコピーする。キャッシュが無ければ false。</summary>
    public bool TryCopyInto(string commitHash, string destination)
    {
        lock (_syncRoot)
        {
            string? source = GetPath(commitHash);
            if (source is null) return false;

            CopyDirectory(source, destination);
            return true;
        }
    }

    /// <summary>ワークスペースをコミット単位で保存する（既にあれば何もしない）。上限超過分は古い順に削除。</summary>
    public void Store(string commitHash, string sourceDirectory)
    {
        lock (_syncRoot)
        {
            string destination = Path.Combine(_root, commitHash);
            if (!Directory.Exists(destination)) CopyDirectory(sourceDirectory, destination);
            EvictExcess();
        }
    }

    private void EvictExcess()
    {
        string[] directories = Directory.GetDirectories(_root);
        if (directories.Length <= MaxCachedCommits) return;

        foreach (string old in directories.OrderBy(Directory.GetLastWriteTimeUtc).Take(directories.Length - MaxCachedCommits))
        {
            TryDelete(old);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 別ジョブが使用中などで削除できなくても致命的ではないため無視する
        }
    }
}
