using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi;

/// <summary>
/// エントリーポイント。CLI引数の解析と、Git確認 → スキップ判定 → パイプライン実行の全体制御を行う。
/// </summary>
internal static class Program
{
    private const int InterruptedExitCode = 130;

    private static async Task<int> Main(string[] args)
    {
        CliOptions? options = CliOptions.Parse(args);
        if (options is null)
        {
            CliOptions.PrintUsage();
            return 1;
        }

        using CancellationTokenSource cancellationSource = new();
        ActiveContainerRegistry containerRegistry = new();
        Console.CancelKeyPress += (_, eventArgs) => RequestShutdown(eventArgs, cancellationSource);

        try
        {
            return await RunAsync(options, containerRegistry, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            return await ShutdownAfterCancellationAsync(containerRegistry);
        }
        catch (Exception exception) when (
            exception is GitCommandException or InvalidPipelineException or ContainerExecutionException)
        {
            ConsoleLogger.WriteError($"💥 実行を中断しました: {exception.Message}");
            return 1;
        }
    }

    /// <summary>Ctrl+C の既定動作（即時プロセス終了）を抑止し、協調的キャンセルへ切り替える。</summary>
    private static void RequestShutdown(ConsoleCancelEventArgs eventArgs, CancellationTokenSource cancellationSource)
    {
        eventArgs.Cancel = true;
        if (cancellationSource.IsCancellationRequested) return;

        ConsoleLogger.WriteWarning("🛑 中断要求（Ctrl+C）を受け付けました。実行中のジョブを停止しています...");
        cancellationSource.Cancel();
    }

    private static async Task<int> ShutdownAfterCancellationAsync(ActiveContainerRegistry containerRegistry)
    {
        ContainerCleanup cleanup = new(containerRegistry);
        await cleanup.StopAndRemoveAllAsync();
        ConsoleLogger.WriteWarning("🛑 ユーザー要求により実行を中断しました。");
        return InterruptedExitCode;
    }

    private static async Task<int> RunAsync(
        CliOptions options, ActiveContainerRegistry containerRegistry, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(options.RepositoryPath);
        if (!Directory.Exists(repositoryPath))
        {
            throw new InvalidPipelineException($"リポジトリが見つかりません: {repositoryPath}");
        }

        GitManager gitManager = new(repositoryPath);
        string commitHash = await gitManager.GetCurrentCommitHashAsync(cancellationToken);
        ConsoleLogger.WriteInfo($"📌 対象コミット: {commitHash}");
        await WarnIfWorkingTreeIsDirtyAsync(gitManager, cancellationToken);

        string dataRootPath = ResolveDataRootPath(options, repositoryPath);
        HistoryManager historyManager = new(dataRootPath);
        if (await ShouldSkipBuildAsync(historyManager, commitHash, options.IsRebuildRequired, cancellationToken))
        {
            return 0;
        }

        ChangeDetector changeDetector = await CreateChangeDetectorAsync(
            gitManager, historyManager, commitHash, repositoryPath, dataRootPath, cancellationToken);
        return await ExecutePipelineAsync(
            options, repositoryPath, commitHash, dataRootPath,
            historyManager, containerRegistry, changeDetector, cancellationToken);
    }

    /// <summary>直近の成功コミットとの差分を取得する。基準がない・取得に失敗した場合は「全ジョブ実行」として扱う。</summary>
    private static async Task<ChangeDetector> CreateChangeDetectorAsync(
        GitManager gitManager, HistoryManager historyManager, string commitHash,
        string repositoryPath, string dataRootPath, CancellationToken cancellationToken)
    {
        string? baselineCommitHash =
            await historyManager.FindLastSuccessfulCommitHashAsync(commitHash, cancellationToken);
        if (baselineCommitHash is null) return new ChangeDetector(null);

        try
        {
            IReadOnlyList<string> changedFilePaths = ExcludeDataDirectory(
                await gitManager.GetChangedFilesAsync(baselineCommitHash, cancellationToken),
                repositoryPath, dataRootPath);
            ConsoleLogger.WriteInfo($"🔍 変更検知: {baselineCommitHash[..8]} との差分は {changedFilePaths.Count} ファイル");
            return new ChangeDetector(changedFilePaths);
        }
        catch (GitCommandException exception)
        {
            ConsoleLogger.WriteWarning($"⚠ 変更検知に失敗したため全ジョブを実行します: {exception.Message}");
            return new ChangeDetector(null);
        }
    }

    /// <summary>CI自身の管理ディレクトリ配下の差分は、変更検知の判定対象から除外する。</summary>
    private static IReadOnlyList<string> ExcludeDataDirectory(
        IReadOnlyList<string> changedFilePaths, string repositoryPath, string dataRootPath)
    {
        string relativeDataPath = Path.GetRelativePath(repositoryPath, dataRootPath);
        if (relativeDataPath.StartsWith("..", StringComparison.Ordinal)) return changedFilePaths;

        string dataDirectoryPrefix = relativeDataPath.Replace('\\', '/') + '/';
        return changedFilePaths
            .Where(path => !path.StartsWith(dataDirectoryPrefix, StringComparison.Ordinal))
            .ToList();
    }

    private static async Task<int> ExecutePipelineAsync(
        CliOptions options, string repositoryPath, string commitHash, string dataRootPath,
        HistoryManager historyManager, ActiveContainerRegistry containerRegistry,
        ChangeDetector changeDetector, CancellationToken cancellationToken)
    {
        PipelineParser parser = new();
        PipelineDefinition pipeline = await parser.ParseAsync(
            Path.Combine(repositoryPath, options.ConfigFileName), cancellationToken);

        ContainerEngine engine = await ResolveEngineAsync(options, cancellationToken);
        ConsoleLogger.WriteInfo($"🐳 コンテナエンジン: {engine.ExecutableName}");

        string logDirectoryPath = historyManager.PrepareLogDirectory(commitHash);
        ContainerRunner containerRunner = new(engine, containerRegistry, repositoryPath, logDirectoryPath);
        ArtifactManager artifactManager = new(repositoryPath, dataRootPath, commitHash);
        DagEngine dagEngine = new(containerRunner, artifactManager, changeDetector);

        IReadOnlyList<JobResult> results = await dagEngine.ExecuteAsync(pipeline, cancellationToken);
        PrintSummary(results, logDirectoryPath);

        bool isSuccess = results.All(result => result.Status is JobStatus.Success or JobStatus.SkippedByChanges);
        await historyManager.SaveRecordAsync(
            commitHash, CreateBuildRecord(isSuccess, logDirectoryPath, results), cancellationToken);
        if (!isSuccess) return 1;

        ConsoleLogger.WriteSuccess("🎉 パイプラインが完了しました。");
        return 0;
    }

    private static async Task<ContainerEngine> ResolveEngineAsync(CliOptions options, CancellationToken cancellationToken)
        => options.EngineName is null
            ? await ContainerEngine.DetectAsync(cancellationToken)
            : ContainerEngine.FromName(options.EngineName)
                ?? throw new ContainerExecutionException($"未対応のコンテナエンジンです: {options.EngineName}");

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
        HistoryManager historyManager, string commitHash, bool isRebuildRequired, CancellationToken cancellationToken)
    {
        if (isRebuildRequired) return false;
        if (!await historyManager.HasSuccessRecordAsync(commitHash, cancellationToken)) return false;

        ConsoleLogger.WriteWarning(
            $"⏭  コミット {commitHash[..8]} は実行済みのためスキップします (--rebuild で強制再実行できます)。");
        return true;
    }

    private static async Task WarnIfWorkingTreeIsDirtyAsync(GitManager gitManager, CancellationToken cancellationToken)
    {
        if (!await gitManager.HasUncommittedChangesAsync(cancellationToken)) return;

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
        JobStatus.Success          => "✅ 成功",
        JobStatus.Failed           => "❌ 失敗",
        JobStatus.Skipped          => "⏭ スキップ",
        JobStatus.SkippedByChanges => "⏭ 変更なし",
        _                          => "⏳ 待機中"
    };
}
