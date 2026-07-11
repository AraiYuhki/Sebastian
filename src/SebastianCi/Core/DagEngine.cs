using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの実効依存関係（needs ＋ ステージ順序）に基づく並列実行の制御だけを担当する。
/// </summary>
public sealed class DagEngine
{
    private readonly ContainerRunner _containerRunner;
    private readonly ArtifactManager _artifactManager;
    private readonly ChangeDetector _changeDetector;
    private readonly int? _maxParallel;

    public DagEngine(
        ContainerRunner containerRunner, ArtifactManager artifactManager,
        ChangeDetector changeDetector, int? maxParallel = null)
    {
        _containerRunner = containerRunner;
        _artifactManager = artifactManager;
        _changeDetector = changeDetector;
        _maxParallel = maxParallel;
    }

    /// <summary>
    /// 依存関係を解決しながら全ジョブを実行する。
    /// 依存のないジョブ同士は Task として同時に走り、Task.WhenAll で合流する。
    /// --max-parallel が指定された場合は、同時に走るコンテナ数をセマフォで制限する。
    /// </summary>
    public async Task<IReadOnlyList<JobResult>> ExecuteAsync(
        PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveNeeds =
            DependencyGraph.BuildEffectiveNeeds(pipeline);
        List<string> sortedJobIds = DependencyGraph.SortTopologically(effectiveNeeds);
        ConsoleLogger.WriteInfo($"🚀 パイプライン '{pipeline.Name}' を開始します (ジョブ数: {sortedJobIds.Count})");

        using SemaphoreSlim? throttle = _maxParallel is int limit ? new SemaphoreSlim(limit, limit) : null;
        Dictionary<string, Task<JobResult>> jobTasks = new();
        foreach (string jobId in sortedJobIds)
        {
            Task<JobResult>[] dependencyTasks =
                effectiveNeeds[jobId].Select(needId => jobTasks[needId]).ToArray();
            jobTasks[jobId] = ExecuteJobAsync(jobId, pipeline.Jobs[jobId], dependencyTasks, throttle, cancellationToken);
        }

        return await Task.WhenAll(jobTasks.Values);
    }

    private async Task<JobResult> ExecuteJobAsync(
        string jobId, JobDefinition job, Task<JobResult>[] dependencyTasks,
        SemaphoreSlim? throttle, CancellationToken cancellationToken)
    {
        JobResult[] dependencyResults = await Task.WhenAll(dependencyTasks);
        if (dependencyResults.Any(result => !IsDependencySatisfied(result.Status)))
        {
            ConsoleLogger.WriteWarning($"⏭  ジョブ '{jobId}' は先行ジョブの失敗によりスキップされました。");
            return new JobResult(jobId, JobStatus.Skipped, TimeSpan.Zero);
        }

        if (!_changeDetector.ShouldRun(job))
        {
            ConsoleLogger.WriteWarning($"⏭  ジョブ '{jobId}' は changes に一致する変更がないためスキップされました。");
            return new JobResult(jobId, JobStatus.SkippedByChanges, TimeSpan.Zero);
        }

        return await RunThrottledAsync(jobId, job, throttle, cancellationToken);
    }

    /// <summary>変更なしスキップは「実行不要だった」だけであり、後続ジョブの実行は妨げない。</summary>
    private static bool IsDependencySatisfied(JobStatus status)
        => status is JobStatus.Success or JobStatus.SkippedByChanges;

    /// <summary>並列度の枠を確保してからジョブを実行する。枠の確保待ちは「開始」表示より前に行う。</summary>
    private async Task<JobResult> RunThrottledAsync(
        string jobId, JobDefinition job, SemaphoreSlim? throttle, CancellationToken cancellationToken)
    {
        if (throttle is not null) await throttle.WaitAsync(cancellationToken);
        try
        {
            return await RunSingleJobAsync(jobId, job, cancellationToken);
        }
        finally
        {
            throttle?.Release();
        }
    }

    private async Task<JobResult> RunSingleJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        ConsoleLogger.WriteInfo($"▶ ジョブ '{jobId}' を開始します (イメージ: {job.Image})");
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            await _containerRunner.RunJobAsync(jobId, job, cancellationToken);
            await _artifactManager.CollectAsync(jobId, job, cancellationToken);
            stopwatch.Stop();
            ConsoleLogger.WriteSuccess($"✅ ジョブ '{jobId}' が成功しました ({stopwatch.Elapsed.TotalSeconds:F1} 秒)");
            return new JobResult(jobId, JobStatus.Success, stopwatch.Elapsed);
        }
        catch (ContainerExecutionException exception)
        {
            stopwatch.Stop();
            ConsoleLogger.WriteError($"❌ ジョブ '{jobId}' が失敗しました: {exception.Message}");
            return new JobResult(jobId, JobStatus.Failed, stopwatch.Elapsed);
        }
    }
}
