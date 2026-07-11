using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの実効依存関係（needs ＋ ステージ順序）に基づく並列実行の制御だけを担当する。
/// </summary>
public sealed class DagEngine
{
    private readonly PodmanRunner _podmanRunner;
    private readonly ArtifactManager _artifactManager;

    public DagEngine(PodmanRunner podmanRunner, ArtifactManager artifactManager)
    {
        _podmanRunner = podmanRunner;
        _artifactManager = artifactManager;
    }

    /// <summary>
    /// 依存関係を解決しながら全ジョブを実行する。
    /// 依存のないジョブ同士は Task として同時に走り、Task.WhenAll で合流する。
    /// </summary>
    public async Task<IReadOnlyList<JobResult>> ExecuteAsync(
        PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveNeeds =
            DependencyGraph.BuildEffectiveNeeds(pipeline);
        List<string> sortedJobIds = DependencyGraph.SortTopologically(effectiveNeeds);
        ConsoleLogger.WriteInfo($"🚀 パイプライン '{pipeline.Name}' を開始します (ジョブ数: {sortedJobIds.Count})");

        Dictionary<string, Task<JobResult>> jobTasks = new();
        foreach (string jobId in sortedJobIds)
        {
            Task<JobResult>[] dependencyTasks =
                effectiveNeeds[jobId].Select(needId => jobTasks[needId]).ToArray();
            jobTasks[jobId] = ExecuteJobAsync(jobId, pipeline.Jobs[jobId], dependencyTasks, cancellationToken);
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
