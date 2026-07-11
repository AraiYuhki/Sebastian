using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブ依存グラフの構築とトポロジカルソート（純粋なグラフ計算）だけを担当する。
/// </summary>
public static class DependencyGraph
{
    /// <summary>
    /// 明示的な needs に、ステージ順序による暗黙の依存
    /// （前ステージの全ジョブ完了を待つ）を加えた実効依存関係を構築する。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildEffectiveNeeds(PipelineDefinition pipeline)
    {
        Dictionary<string, IReadOnlyList<string>> effectiveNeeds = new();
        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            effectiveNeeds[jobId] = job.Needs
                .Concat(CollectEarlierStageJobIds(job, pipeline))
                .Distinct()
                .ToList();
        }

        return effectiveNeeds;
    }

    /// <summary>
    /// Kahn法でトポロジカルソートする。解決できないジョブが残った場合は
    /// 循環参照とみなして InvalidPipelineException をスローする。
    /// </summary>
    public static List<string> SortTopologically(IReadOnlyDictionary<string, IReadOnlyList<string>> needsByJobId)
    {
        Dictionary<string, int> remainingDependencyCounts =
            needsByJobId.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        Queue<string> readyJobIds = new(
            remainingDependencyCounts.Where(pair => pair.Value == 0).Select(pair => pair.Key));

        List<string> sortedJobIds = new();
        while (readyJobIds.TryDequeue(out string? resolvedJobId))
        {
            sortedJobIds.Add(resolvedJobId);
            EnqueueUnblockedJobs(resolvedJobId, needsByJobId, remainingDependencyCounts, readyJobIds);
        }

        if (sortedJobIds.Count != needsByJobId.Count)
        {
            IEnumerable<string> unresolvedJobIds =
                remainingDependencyCounts.Where(pair => pair.Value > 0).Select(pair => pair.Key);
            throw new InvalidPipelineException(
                $"needs に循環参照（循環依存）が存在します。対象ジョブ: {string.Join(", ", unresolvedJobIds)}");
        }

        return sortedJobIds;
    }

    private static IEnumerable<string> CollectEarlierStageJobIds(JobDefinition job, PipelineDefinition pipeline)
    {
        if (pipeline.Stages.Count == 0) return Enumerable.Empty<string>();

        int jobStageIndex = pipeline.Stages.IndexOf(job.Stage);
        return pipeline.Jobs
            .Where(pair => pipeline.Stages.IndexOf(pair.Value.Stage) < jobStageIndex)
            .Select(pair => pair.Key);
    }

    private static void EnqueueUnblockedJobs(
        string resolvedJobId, IReadOnlyDictionary<string, IReadOnlyList<string>> needsByJobId,
        Dictionary<string, int> remainingDependencyCounts, Queue<string> readyJobIds)
    {
        foreach ((string jobId, IReadOnlyList<string> needs) in needsByJobId)
        {
            if (!needs.Contains(resolvedJobId)) continue;
            if (--remainingDependencyCounts[jobId] == 0) readyJobIds.Enqueue(jobId);
        }
    }
}
