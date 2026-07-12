using SebastianCi.Core;
using SebastianCi.Models;
using SebastianCi.Web;

namespace SebastianCi;

/// <summary>
/// エントリーポイント。CLI引数の解析と、Git確認 → スキップ判定 → パイプライン実行の全体制御を行う。
/// </summary>
internal static class Program
{
    private const int InterruptedExitCode = 130;
    private const string CacheDirectoryName = "cache";
    private const string PluginsDirectoryName = "plugins";

    private static async Task<int> Main(string[] args)
    {
        if (args is ["serve", .. var serveArgs]) return await RunServeAsync(serveArgs);
        if (args is ["agent", .. var agentArgs]) return await RunAgentAsync(agentArgs);

        CliOptions? options = CliOptions.Parse(args);
        if (options is null)
        {
            CliOptions.PrintUsage();
            return 1;
        }

        using CancellationTokenSource cancellationSource = new();
        ActiveContainerRegistry containerRegistry = new();
        Console.CancelKeyPress += (_, eventArgs) => RequestShutdown(eventArgs, cancellationSource);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupRemainingContainers(containerRegistry);

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

    /// <summary>`serve` サブコマンド：ブラウザ用ダッシュボードを提供するWebサーバーを起動する。</summary>
    private static async Task<int> RunServeAsync(string[] serveArgs)
    {
        ServeOptions? options = ServeOptions.Parse(serveArgs);
        if (options is null)
        {
            Console.WriteLine("使い方: sebastian-ci serve [リポジトリパス] [--port <番号>] [--config <ファイル名>] [--data-dir <パス>]");
            return 1;
        }

        if (!Directory.Exists(Path.GetFullPath(options.RepositoryPath)))
        {
            ConsoleLogger.WriteError($"💥 リポジトリが見つかりません: {options.RepositoryPath}");
            return 1;
        }

        await new WebServer(options).RunAsync();
        return 0;
    }

    /// <summary>`agent` サブコマンド：他マシンからのジョブを受けて実行するエージェントを起動する。</summary>
    private static async Task<int> RunAgentAsync(string[] agentArgs)
    {
        AgentOptions? options = AgentOptions.Parse(agentArgs);
        if (options is null)
        {
            Console.WriteLine("使い方: sebastian-ci agent [ワークスペースのパス] [--port <番号>] [--engine <podman|docker>] [--token <トークン>]");
            return 1;
        }

        try
        {
            AgentServer agent = await AgentServer.CreateAsync(options);
            await agent.RunAsync();
            return 0;
        }
        catch (ContainerExecutionException exception)
        {
            ConsoleLogger.WriteError($"💥 エージェントを起動できません: {exception.Message}");
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

    /// <summary>
    /// SIGTERM や未処理例外などでプロセスが終了する際の最終防衛線。
    /// 追跡中のコンテナが残っていれば停止・削除してから終了する。
    /// ProcessExit ハンドラーは同期実行のため、完了を待機しないとコンテナが残る。
    /// </summary>
    private static void CleanupRemainingContainers(ActiveContainerRegistry containerRegistry)
    {
        ContainerCleanup cleanup = new(containerRegistry);
        cleanup.StopAndRemoveAllAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        CliOptions options, ActiveContainerRegistry containerRegistry, CancellationToken cancellationToken)
    {
        string repositoryPath = Path.GetFullPath(options.RepositoryPath);
        if (!Directory.Exists(repositoryPath))
        {
            throw new InvalidPipelineException($"リポジトリが見つかりません: {repositoryPath}");
        }

        if (options.IsValidateOnly) return await ValidateOnlyAsync(options, repositoryPath, cancellationToken);

        GitManager gitManager = new(repositoryPath);
        string commitHash = await gitManager.GetCurrentCommitHashAsync(cancellationToken);
        ConsoleLogger.WriteInfo($"📌 対象コミット: {commitHash}");
        await WarnIfWorkingTreeIsDirtyAsync(gitManager, cancellationToken);

        string dataRootPath = ResolveDataRootPath(options, repositoryPath);
        HistoryManager historyManager = new(dataRootPath);
        if (await ShouldSkipBuildAsync(historyManager, commitHash, options, cancellationToken))
        {
            return 0;
        }

        ChangeDetector changeDetector = await CreateChangeDetectorAsync(
            gitManager, historyManager, commitHash, repositoryPath, dataRootPath, cancellationToken);
        return await ExecutePipelineAsync(
            options, repositoryPath, commitHash, dataRootPath,
            historyManager, containerRegistry, changeDetector, cancellationToken);
    }

    /// <summary>
    /// 構成ファイルの検証だけを行う（--validate）。git・コンテナ・履歴に一切触れず、
    /// パース・スキーマ検証・matrix展開・依存関係（循環）チェックまで通れば成功とする。
    /// </summary>
    private static async Task<int> ValidateOnlyAsync(
        CliOptions options, string repositoryPath, CancellationToken cancellationToken)
    {
        PipelineParser parser = new();
        PipelineDefinition pipeline = await parser.ParseAsync(
            Path.Combine(repositoryPath, options.ConfigFileName), cancellationToken);
        SelectTargetJobs(pipeline, options);
        DependencyGraph.SortTopologically(DependencyGraph.BuildEffectiveNeeds(pipeline));

        ConsoleLogger.WriteSuccess($"✅ 構成は正常です（ジョブ数: {pipeline.Jobs.Count}）");
        foreach (string jobId in pipeline.Jobs.Keys)
        {
            ConsoleLogger.WriteInfo($"  • {jobId}");
        }

        return 0;
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
        SelectTargetJobs(pipeline, options);

        new SystemResourceMonitor(pipeline.Resources).ReportAndWarn(repositoryPath);

        LoadedPlugins plugins = await LoadPluginsAsync(pipeline, dataRootPath, cancellationToken);
        string logDirectoryPath = historyManager.PrepareLogDirectory(commitHash);
        IReadOnlyDictionary<string, IJobRunner> pluginRunners =
            BuildPluginRunners(plugins, pipeline, repositoryPath, logDirectoryPath);

        using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        using HttpClient agentHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
        List<INotificationChannel> channels = [new SlackNotifier(httpClient), new ChatWorkNotifier(httpClient)];
        channels.AddRange(plugins.Channels);
        EnsureNotificationTypesResolvable(pipeline.Notifications, channels);
        NotificationDispatcher dispatcher = new(pipeline.Notifications, channels);
        await dispatcher.DispatchAsync(
            NotificationEvent.Start, BuildNotificationMessage(NotificationEvent.Start, pipeline, commitHash, null),
            cancellationToken);

        WorkspaceSource? workspaceSource = CreateWorkspaceSourceIfNeeded(pipeline, repositoryPath, commitHash);
        DagEngine dagEngine = BuildDagEngine(
            await ResolveEngineAsync(options, cancellationToken), agentHttpClient, workspaceSource, pipeline.Agents,
            pluginRunners, options, repositoryPath, dataRootPath, commitHash, logDirectoryPath,
            containerRegistry, changeDetector);

        IReadOnlyList<JobResult> results = await dagEngine.ExecuteAsync(pipeline, cancellationToken);
        PrintSummary(results, logDirectoryPath);

        bool isSuccess = results.All(result =>
            result.Status is JobStatus.Success or JobStatus.SkippedByChanges or JobStatus.FailedIgnored);
        await SaveRecordIfFullRunAsync(
            historyManager, commitHash, isSuccess, logDirectoryPath, results, options, cancellationToken);
        await NotifyResultAsync(dispatcher, isSuccess, pipeline, commitHash, results, cancellationToken);

        if (!isSuccess) return 1;
        ConsoleLogger.WriteSuccess("🎉 パイプラインが完了しました。");
        return 0;
    }

    private static DagEngine BuildDagEngine(
        ContainerEngine engine, HttpClient agentHttpClient, WorkspaceSource? workspaceSource,
        IReadOnlyList<AgentEndpoint> agents, IReadOnlyDictionary<string, IJobRunner> pluginRunners,
        CliOptions options, string repositoryPath, string dataRootPath, string commitHash, string logDirectoryPath,
        ActiveContainerRegistry containerRegistry, ChangeDetector changeDetector)
    {
        ConsoleLogger.WriteInfo($"🐳 コンテナエンジン: {engine.ExecutableName}");
        string cacheRootPath = Path.Combine(dataRootPath, CacheDirectoryName);
        ContainerRunner containerRunner =
            new(engine, containerRegistry, repositoryPath, logDirectoryPath, cacheRootPath);
        RemoteAgentRunner sender = new(agentHttpClient, workspaceSource);
        JobRunnerSelector runnerSelector = new(
            containerRunner, new DirectAgentRunner(sender),
            new PooledAgentRunner(new AgentPool(agents), sender), pluginRunners);
        ArtifactManager artifactManager = new(repositoryPath, dataRootPath, commitHash);
        return new DagEngine(runnerSelector, artifactManager, changeDetector, options.MaxParallel);
    }

    /// <summary>plugins が指定されていれば読み込んで拡張点の実装を返す。無ければ空。</summary>
    private static async Task<LoadedPlugins> LoadPluginsAsync(
        PipelineDefinition pipeline, string dataRootPath, CancellationToken cancellationToken)
    {
        if (pipeline.Plugins.Count == 0) return new LoadedPlugins([], []);

        PluginLoader loader = new(Path.Combine(dataRootPath, PluginsDirectoryName));
        LoadedPlugins loaded = await loader.LoadAsync(pipeline.Plugins, cancellationToken);
        ConsoleLogger.WriteInfo(
            $"🔌 プラグイン読み込み: 通知チャンネル {loaded.Channels.Count} 件・ジョブランナー {loaded.JobRunners.Count} 件");
        return loaded;
    }

    /// <summary>プラグインのジョブランナーを名前→IJobRunner の辞書に整える。runner の解決可否も検証する。</summary>
    private static IReadOnlyDictionary<string, IJobRunner> BuildPluginRunners(
        LoadedPlugins plugins, PipelineDefinition pipeline, string repositoryPath, string logDirectoryPath)
    {
        Dictionary<string, IJobRunner> runners = plugins.JobRunners.ToDictionary(
            runner => runner.Name,
            runner => (IJobRunner)new PluginJobRunnerAdapter(runner, repositoryPath, logDirectoryPath));

        string? unresolved = pipeline.Jobs.Values
            .Select(job => job.Runner)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !runners.ContainsKey(name));
        if (unresolved is not null)
        {
            throw new InvalidPipelineException(
                $"runner '{unresolved}' に対応するプラグインが読み込まれていません（plugins の指定を確認してください）。");
        }

        return runners;
    }

    /// <summary>リモート実行（agent 指定 または remote）ジョブがある場合のみ、ワークスペース供給元を用意する。</summary>
    private static WorkspaceSource? CreateWorkspaceSourceIfNeeded(
        PipelineDefinition pipeline, string repositoryPath, string commitHash)
    {
        bool hasRemoteJob = pipeline.Jobs.Values.Any(job => !string.IsNullOrEmpty(job.Agent) || job.Remote);
        return hasRemoteJob ? new WorkspaceSource(new GitManager(repositoryPath), commitHash) : null;
    }

    private static void EnsureNotificationTypesResolvable(
        List<NotificationConfig> notifications, IReadOnlyList<INotificationChannel> channels)
    {
        HashSet<string> available = channels.Select(channel => channel.Type).ToHashSet();
        string? unresolved = notifications.Select(n => n.Type).FirstOrDefault(type => !available.Contains(type));
        if (unresolved is not null)
        {
            throw new InvalidPipelineException(
                $"notifications の type '{unresolved}' に対応するチャンネルがありません（plugins の指定を確認してください）。");
        }
    }

    private static async Task NotifyResultAsync(
        NotificationDispatcher dispatcher, bool isSuccess, PipelineDefinition pipeline,
        string commitHash, IReadOnlyList<JobResult> results, CancellationToken cancellationToken)
    {
        NotificationEvent resultEvent = isSuccess ? NotificationEvent.Success : NotificationEvent.Failure;
        await dispatcher.DispatchAsync(
            resultEvent, BuildNotificationMessage(resultEvent, pipeline, commitHash, results), cancellationToken);
    }

    private static string BuildNotificationMessage(
        NotificationEvent triggeredEvent, PipelineDefinition pipeline,
        string commitHash, IReadOnlyList<JobResult>? results)
    {
        string shortHash = commitHash[..Math.Min(8, commitHash.Length)];
        return triggeredEvent switch
        {
            NotificationEvent.Start   => $"🚀 [{pipeline.Name}] を開始しました (コミット: {shortHash})",
            NotificationEvent.Success => $"✅ [{pipeline.Name}] が成功しました (コミット: {shortHash})",
            NotificationEvent.Failure =>
                $"❌ [{pipeline.Name}] が失敗しました (コミット: {shortHash}, 失敗ジョブ: {FormatFailedJobs(results)})",
            _ => pipeline.Name
        };
    }

    private static string FormatFailedJobs(IReadOnlyList<JobResult>? results)
    {
        if (results is null) return "";

        return string.Join(", ", results.Where(r => r.Status is JobStatus.Failed).Select(r => r.JobId));
    }

    private static void SelectTargetJobs(PipelineDefinition pipeline, CliOptions options)
    {
        if (options.TargetJobIds.Count == 0) return;

        JobSelector.SelectTargets(pipeline, options.TargetJobIds);
        ConsoleLogger.WriteInfo($"🎯 対象ジョブ（依存含む）: {string.Join(", ", pipeline.Jobs.Keys)}");
    }

    /// <summary>--job による部分実行は全体の成功を意味しないため、履歴には記録しない。</summary>
    private static async Task SaveRecordIfFullRunAsync(
        HistoryManager historyManager, string commitHash, bool isSuccess, string logDirectoryPath,
        IReadOnlyList<JobResult> results, CliOptions options, CancellationToken cancellationToken)
    {
        if (options.TargetJobIds.Count > 0)
        {
            ConsoleLogger.WriteInfo("ℹ --job による部分実行のため、実行履歴（スキップ判定）は更新しません。");
            return;
        }

        await historyManager.SaveRecordAsync(
            commitHash, CreateBuildRecord(isSuccess, logDirectoryPath, results), cancellationToken);
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
        HistoryManager historyManager, string commitHash, CliOptions options, CancellationToken cancellationToken)
    {
        if (options.IsRebuildRequired) return false;
        if (options.TargetJobIds.Count > 0) return false;
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
        JobStatus.FailedIgnored    => "⚠ 失敗(許容)",
        JobStatus.Skipped          => "⏭ スキップ",
        JobStatus.SkippedByChanges => "⏭ 変更なし",
        _                          => "⏳ 待機中"
    };
}
