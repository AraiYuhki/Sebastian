using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブ依存関係（DAG）のトポロジカルソートと並列実行の制御だけを担当する。
/// </summary>
public sealed class DagEngine
{
    private readonly PodmanRunner _podmanRunner;

    public DagEngine(PodmanRunner podmanRunner) => _podmanRunner = podmanRunner;

    /// <summary>
    /// 依存関係を解決しながら全ジョブを実行する。
    /// 依存のないジョブ同士は Task として同時に走り、Task.WhenAll で合流する。
    /// </summary>
    public async Task<IReadOnlyList<JobResult>> ExecuteAsync(
        PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        List<string> sortedJobIds = SortTopologically(pipeline.Jobs);
        ConsoleLogger.WriteInfo($"🚀 パイプライン '{pipeline.Name}' を開始します (ジョブ数: {sortedJobIds.Count})");

        Dictionary<string, Task<JobResult>> jobTasks = new();
        foreach (string jobId in sortedJobIds)
        {
            JobDefinition job = pipeline.Jobs[jobId];
            Task<JobResult>[] dependencyTasks = job.Needs.Select(needId => jobTasks[needId]).ToArray();
            jobTasks[jobId] = ExecuteJobAsync(jobId, job, dependencyTasks, cancellationToken);
        }

        return await Task.WhenAll(jobTasks.Values);
    }

    private async Task<JobResult> ExecuteJobAsync(
        string jobId, JobDefinition job, Task<JobResult>[] dependencyTasks, CancellationToken cancellationToken)
    {
        JobResult[] dependencyResults = await Task.WhenAll(dependencyTasks);
        if (dependencyResults.Any(result => result.Status is not JobStatus.Success))
        {
            ConsoleLogger.WriteWarning($"⏭  ジョブ '{jobId}' は先行ジョブの失敗によりスキップされました。");
            return new JobResult(jobId, JobStatus.Skipped, TimeSpan.Zero);
        }

        return await RunSingleJobAsync(jobId, job, cancellationToken);
    }

    private async Task<JobResult> RunSingleJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        ConsoleLogger.WriteInfo($"▶ ジョブ '{jobId}' を開始します (イメージ: {job.Image})");
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            await _podmanRunner.RunJobAsync(jobId, job, cancellationToken);
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

    private static List<string> SortTopologically(IReadOnlyDictionary<string, JobDefinition> jobs)
    {
        Dictionary<string, int> remainingDependencyCounts =
            jobs.ToDictionary(pair => pair.Key, pair => pair.Value.Needs.Count);
        Queue<string> readyJobIds = new(
            remainingDependencyCounts.Where(pair => pair.Value == 0).Select(pair => pair.Key));

        List<string> sortedJobIds = new();
        while (readyJobIds.TryDequeue(out string? completedJobId))
        {
            sortedJobIds.Add(completedJobId);
            EnqueueUnblockedJobs(jobs, completedJobId, remainingDependencyCounts, readyJobIds);
        }

        if (sortedJobIds.Count != jobs.Count)
        {
            throw new PipelineValidationException("ジョブの依存関係に循環（サイクル）が存在します。needs を見直してください。");
        }

        return sortedJobIds;
    }

    private static void EnqueueUnblockedJobs(
        IReadOnlyDictionary<string, JobDefinition> jobs, string completedJobId,
        Dictionary<string, int> remainingDependencyCounts, Queue<string> readyJobIds)
    {
        foreach ((string jobId, JobDefinition job) in jobs)
        {
            if (!job.Needs.Contains(completedJobId)) continue;
            if (--remainingDependencyCounts[jobId] == 0) readyJobIds.Enqueue(jobId);
        }
    }
}
