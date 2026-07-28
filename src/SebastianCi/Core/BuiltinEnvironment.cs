using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// CI実行のコンテキスト（パイプライン名・ジョブID・コミット・ブランチ）を表す組み込み環境変数を、
/// 各ジョブの env に注入する処理だけを担当する。ユーザーが同名のキーを定義している場合はそちらが優先される。
/// </summary>
public static class BuiltinEnvironment
{
    private const int ShortHashLength = 8;

    /// <summary>全ジョブの env に組み込み環境変数を追加する（既存キーは上書きしない）。</summary>
    public static void Apply(PipelineDefinition pipeline, string commitHash, string branchName)
    {
        string shortHash = commitHash[..Math.Min(ShortHashLength, commitHash.Length)];
        foreach ((string jobId, JobDefinition job) in pipeline.Jobs)
        {
            AddIfAbsent(job.Env, "CI", "true");
            AddIfAbsent(job.Env, "SEBASTIAN_CI", "true");
            AddIfAbsent(job.Env, "SEBASTIAN_CI_PIPELINE", pipeline.Name);
            AddIfAbsent(job.Env, "SEBASTIAN_CI_JOB", jobId);
            AddIfAbsent(job.Env, "SEBASTIAN_CI_COMMIT", commitHash);
            AddIfAbsent(job.Env, "SEBASTIAN_CI_COMMIT_SHORT", shortHash);
            AddIfAbsent(job.Env, "SEBASTIAN_CI_BRANCH", branchName);
        }
    }

    private static void AddIfAbsent(Dictionary<string, string> env, string key, string value)
    {
        if (!env.ContainsKey(key)) env[key] = value;
    }
}
