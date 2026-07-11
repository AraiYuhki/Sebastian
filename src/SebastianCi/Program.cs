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
            exception is GitCommandException or PipelineValidationException or ContainerExecutionException)
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
            throw new PipelineValidationException($"リポジトリが見つかりません: {repositoryPath}");
        }

        GitManager gitManager = new(repositoryPath);
        string commitHash = await gitManager.GetCurrentCommitHashAsync();
        ConsoleLogger.WriteInfo($"📌 対象コミット: {commitHash}");
        await WarnIfWorkingTreeIsDirtyAsync(gitManager);

        BuildHistoryManager historyManager = new(repositoryPath);
        if (ShouldSkipBuild(historyManager, commitHash, options.IsRebuildRequired)) return 0;

        return await ExecutePipelineAsync(options, repositoryPath, commitHash, historyManager);
    }

    private static async Task<int> ExecutePipelineAsync(
        CliOptions options, string repositoryPath, string commitHash, BuildHistoryManager historyManager)
    {
        PipelineParser parser = new();
        PipelineDefinition pipeline = await parser.ParseAsync(Path.Combine(repositoryPath, options.ConfigFileName));

        string logDirectoryPath = historyManager.PrepareLogDirectory(commitHash);
        PodmanRunner podmanRunner = new(repositoryPath, logDirectoryPath);
        DagEngine dagEngine = new(podmanRunner);

        IReadOnlyList<JobResult> results = await dagEngine.ExecuteAsync(pipeline);
        PrintSummary(results, logDirectoryPath);

        if (results.Any(result => result.Status is not JobStatus.Success)) return 1;

        await historyManager.SaveSuccessRecordAsync(commitHash);
        ConsoleLogger.WriteSuccess("🎉 パイプラインが完了しました。");
        return 0;
    }

    private static bool ShouldSkipBuild(BuildHistoryManager historyManager, string commitHash, bool isRebuildRequired)
    {
        if (isRebuildRequired) return false;
        if (!historyManager.HasSuccessRecord(commitHash)) return false;

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
