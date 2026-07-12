using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// matrix を持つジョブを、全組み合わせ分の具象ジョブへ展開する処理だけを担当する。
/// 特別なキー image はコンテナイメージを切り替え、その他のキーは環境変数として注入される。
/// 展開後、他ジョブの needs は全バリアントへの依存に書き換えられる。
/// </summary>
public static class MatrixExpander
{
    public const string ImageKey = "image";
    public const int MaxCombinationCount = 50;

    public static void Expand(PipelineDefinition pipeline)
    {
        Dictionary<string, List<string>> expandedIdsByOriginalId = new();
        Dictionary<string, JobDefinition> expandedJobs = new();

        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            List<(string VariantId, JobDefinition Variant)> variants = ExpandJob(jobId, job);
            expandedIdsByOriginalId[jobId] = variants.Select(variant => variant.VariantId).ToList();
            foreach ((string variantId, JobDefinition variant) in variants)
            {
                expandedJobs[variantId] = variant;
            }
        }

        RewriteNeeds(expandedJobs, expandedIdsByOriginalId);
        pipeline.Jobs = expandedJobs;
    }

    private static List<(string VariantId, JobDefinition Variant)> ExpandJob(string jobId, JobDefinition job)
    {
        if (job.Matrix.Count == 0) return [(jobId, job)];

        return BuildCombinations(job.Matrix)
            .Select(combination => (BuildVariantId(jobId, combination), BuildVariant(job, combination)))
            .ToList();
    }

    private static List<Dictionary<string, string>> BuildCombinations(Dictionary<string, List<string>> matrix)
    {
        List<Dictionary<string, string>> combinations = [new()];
        foreach ((string key, List<string> values) in matrix)
        {
            combinations = combinations
                .SelectMany(combination => values.Select(value =>
                    new Dictionary<string, string>(combination) { [key] = value }))
                .ToList();
        }

        return combinations;
    }

    private static string BuildVariantId(string jobId, Dictionary<string, string> combination)
    {
        IEnumerable<string> labels = combination.Select(pair => $"{pair.Key}={pair.Value}");
        return $"{jobId}[{string.Join(", ", labels)}]";
    }

    private static JobDefinition BuildVariant(JobDefinition job, Dictionary<string, string> combination)
    {
        Dictionary<string, string> variantEnv = new(job.Env);
        foreach ((string key, string value) in combination.Where(pair => pair.Key != ImageKey))
        {
            variantEnv[key] = value;
        }

        return new JobDefinition
        {
            Image = combination.GetValueOrDefault(ImageKey, job.Image),
            Stage = job.Stage,
            Needs = new(job.Needs),
            Script = new(job.Script),
            Env = variantEnv,
            Artifacts = new(job.Artifacts),
            Changes = new(job.Changes),
            Timeout = job.Timeout,
            Retry = job.Retry,
            ContinueOnError = job.ContinueOnError,
            Cache = new(job.Cache),
            Agent = job.Agent,
            AgentToken = job.AgentToken,
            Remote = job.Remote,
            Runner = job.Runner
        };
    }

    private static void RewriteNeeds(
        Dictionary<string, JobDefinition> expandedJobs, Dictionary<string, List<string>> expandedIdsByOriginalId)
    {
        foreach (JobDefinition job in expandedJobs.Values)
        {
            job.Needs = job.Needs.SelectMany(needId => expandedIdsByOriginalId[needId]).ToList();
        }
    }
}
