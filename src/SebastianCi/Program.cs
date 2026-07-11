using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi;

/// <summary>
/// エントリーポイント。CLI引数の解析と、Git確認 → スキップ判定 → パイプライン実行の全体制御を行う。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CliOptions? options = CliOptions.Parse(args);
        if (options is null)
        {
            CliOptions.PrintUsage();
            return 1;
        }

        try
        {
            return await RunAsync(options);
        }
        catch (Exception exception) when (
            exception is GitCommandException or InvalidPipelineException or ContainerExecutionException)
        {
            ConsoleLogger.WriteError($"💥 実行を中断しました: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(CliOptions options)
    {
        string repositoryPath = Path.GetFullPath(options.RepositoryPath);
        if (!Directory.Exists(repositoryPath))
        {
            throw new InvalidPipelineException($"リポジトリが見つかりません: {repositoryPath}");
        }

        GitManager gitManager = new(repositoryPath);
        string commitHash = await gitManager.GetCurrentCommitHashAsync();
        ConsoleLogger.WriteInfo($"📌 対象コミット: {commitHash}");
        await WarnIfWorkingTreeIsDirtyAsync(gitManager);

        string dataRootPath = ResolveDataRootPath(options, repositoryPath);
        HistoryManager historyManager = new(dataRootPath);
        if (await ShouldSkipBuildAsync(historyManager, commitHash, options.IsRebuildRequired)) return 0;

        return await ExecutePipelineAsync(options, repositoryPath, commitHash, dataRootPath, historyManager);
    }

    private static async Task<int> ExecutePipelineAsync(
        CliOptions options, string repositoryPath, string commitHash, string dataRootPath, HistoryManager historyManager)
    {
        PipelineParser parser = new();
        PipelineDefinition pipeline = await parser.ParseAsync(Path.Combine(repositoryPath, options.ConfigFileName));

        string logDirectoryPath = historyManager.PrepareLogDirectory(commitHash);
        PodmanRunner podmanRunner = new(repositoryPath, logDirectoryPath);
        ArtifactManager artifactManager = new(repositoryPath, dataRootPath, commitHash);
        DagEngine dagEngine = new(podmanRunner, artifactManager);

        IReadOnlyList<JobResult> results = await dagEngine.ExecuteAsync(pipeline);
        PrintSummary(results, logDirectoryPath);

        bool isSuccess = results.All(result => result.Status is JobStatus.Success);
        await historyManager.SaveRecordAsync(commitHash, CreateBuildRecord(isSuccess, logDirectoryPath, results));
        if (!isSuccess) return 1;

        ConsoleLogger.WriteSuccess("🎉 パイプラインが完了しました。");
        return 0;
    }

    private static string ResolveDataRootPath(CliOptions options, string repositoryPath)
        => options.DataDirectoryPath is null
            ? Path.Combine(repositoryPath, HistoryManager.DefaultDataDirectoryName)
            : Path.GetFullPath(options.DataDirectoryPath);

    private static BuildRecord CreateBuildRecord(
        bool isSuccess, string logDirectoryPath, IReadOnlyList<JobResult> results)
        => new(
            DateTimeOffset.Now,
            isSuccess,
            logDirectoryPath,
            results.Select(result =>
                new JobRecord(result.JobId, result.Status, Math.Round(result.Duration.TotalSeconds, 1))).ToList());

    private static async Task<bool> ShouldSkipBuildAsync(
        HistoryManager historyManager, string commitHash, bool isRebuildRequired)
    {
        if (isRebuildRequired) return false;
        if (!await historyManager.HasSuccessRecordAsync(commitHash)) return false;

        ConsoleLogger.WriteWarning(
            $"⏭  コミット {commitHash[..8]} は実行済みのためスキップします (--rebuild で強制再実行できます)。");
        return true;
    }

    private static async Task WarnIfWorkingTreeIsDirtyAsync(GitManager gitManager)
    {
        if (!await gitManager.HasUncommittedChangesAsync()) return;

        ConsoleLogger.WriteWarning("⚠ 未コミットの変更があります。実行結果は現在のコミット内容と一致しない可能性があります。");
    }

    private static void PrintSummary(IReadOnlyList<JobResult> results, string logDirectoryPath)
    {
        ConsoleLogger.WriteInfo("―――― 実行結果サマリー ――――");
        foreach (JobResult result in results)
        {
            PrintJobResult(result);
        }

        ConsoleLogger.WriteInfo($"📄 ログ出力先: {logDirectoryPath}");
    }

    private static void PrintJobResult(JobResult result)
    {
        string message = $"{GetStatusLabel(result.Status)} {result.JobId} ({result.Duration.TotalSeconds:F1} 秒)";
        Action<string> write = result.Status switch
        {
            JobStatus.Success => ConsoleLogger.WriteSuccess,
            JobStatus.Failed  => ConsoleLogger.WriteError,
            _                 => ConsoleLogger.WriteWarning
        };
        write(message);
    }

    private static string GetStatusLabel(JobStatus status) => status switch
    {
        JobStatus.Success => "✅ 成功",
        JobStatus.Failed  => "❌ 失敗",
        JobStatus.Skipped => "⏭ スキップ",
        _                 => "⏳ 待機中"
    };
}
