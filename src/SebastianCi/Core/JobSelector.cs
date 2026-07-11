using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// --job で指定されたジョブと、その依存（needs の推移的閉包）だけへの絞り込みを担当する。
/// マトリックス展開後の呼び出しを想定し、元のジョブIDを指定すると全バリアントが対象になる。
/// </summary>
public static class JobSelector
{
    /// <summary>対象指定がある場合、パイプラインを「対象ジョブ＋依存ジョブ」だけに絞り込む。</summary>
    public static void SelectTargets(PipelineDefinition pipeline, IReadOnlyList<string> targetJobIds)
    {
        if (targetJobIds.Count == 0) return;

        HashSet<string> selectedJobIds = ResolveTargets(pipeline, targetJobIds);
        CollectDependenciesTransitively(pipeline, selectedJobIds);
        pipeline.Jobs = pipeline.Jobs
            .Where(pair => selectedJobIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static HashSet<string> ResolveTargets(PipelineDefinition pipeline, IReadOnlyList<string> targetJobIds)
    {
        HashSet<string> resolvedJobIds = new();
        foreach (string targetJobId in targetJobIds)
        {
            resolvedJobIds.UnionWith(ResolveSingleTarget(pipeline, targetJobId));
        }

        return resolvedJobIds;
    }

    private static List<string> ResolveSingleTarget(PipelineDefinition pipeline, string targetJobId)
    {
        // マトリックス展開後のID（例: test[image=...]）は元ID（test）でまとめて指定できる
        List<string> matchedJobIds = pipeline.Jobs.Keys
            .Where(jobId => jobId == targetJobId
                || jobId.StartsWith($"{targetJobId}[", StringComparison.Ordinal))
            .ToList();
        if (matchedJobIds.Count == 0)
        {
            throw new InvalidPipelineException($"--job で指定されたジョブ '{targetJobId}' は定義されていません。");
        }

        return matchedJobIds;
    }

    private static void CollectDependenciesTransitively(PipelineDefinition pipeline, HashSet<string> selectedJobIds)
    {
        Queue<string> pendingJobIds = new(selectedJobIds);
        while (pendingJobIds.TryDequeue(out string? jobId))
        {
            EnqueueUnvisitedNeeds(pipeline.Jobs[jobId].Needs, selectedJobIds, pendingJobIds);
        }
    }

    private static void EnqueueUnvisitedNeeds(
        List<string> needs, HashSet<string> selectedJobIds, Queue<string> pendingJobIds)
    {
        foreach (string needId in needs)
        {
            if (selectedJobIds.Add(needId)) pendingJobIds.Enqueue(needId);
        }
    }
}
