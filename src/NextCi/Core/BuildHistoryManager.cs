namespace NextCi.Core;

/// <summary>
/// コミットハッシュ単位のビルド履歴（成功マーカーとログ置き場）だけを管理する。
/// </summary>
public sealed class BuildHistoryManager
{
    private const string HistoryDirectoryName = ".next-ci";
    private const string BuildsDirectoryName = "builds";
    private const string SuccessMarkerFileName = "success.marker";

    private readonly string _historyRootPath;

    public BuildHistoryManager(string repositoryPath)
        => _historyRootPath = Path.Combine(repositoryPath, HistoryDirectoryName, BuildsDirectoryName);

    /// <summary>指定コミットで過去に成功した形跡（マーカーファイル）があるかを返す。</summary>
    public bool HasSuccessRecord(string commitHash) => File.Exists(GetSuccessMarkerPath(commitHash));

    /// <summary>指定コミット用のログディレクトリを作成し、そのパスを返す。</summary>
    public string PrepareLogDirectory(string commitHash)
    {
        string logDirectoryPath = Path.Combine(_historyRootPath, commitHash);
        Directory.CreateDirectory(logDirectoryPath);
        return logDirectoryPath;
    }

    /// <summary>パイプライン全体の成功を記録する（次回以降のスキップ判定に使う）。</summary>
    public async Task SaveSuccessRecordAsync(string commitHash, CancellationToken cancellationToken = default)
    {
        string markerPath = GetSuccessMarkerPath(commitHash);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(markerPath, DateTimeOffset.Now.ToString("O"), cancellationToken);
    }

    private string GetSuccessMarkerPath(string commitHash)
        => Path.Combine(_historyRootPath, commitHash, SuccessMarkerFileName);
}
