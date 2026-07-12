using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの指定に応じて実行先を選ぶだけを担当する。
/// agent（明示URL）→ 直接委譲、remote → プール割り当て、いずれも無し → ローカル実行。
/// </summary>
public sealed class JobRunnerSelector
{
    private readonly IJobRunner _localRunner;
    private readonly IJobRunner _directRunner;
    private readonly IJobRunner _pooledRunner;

    public JobRunnerSelector(IJobRunner localRunner, IJobRunner directRunner, IJobRunner pooledRunner)
    {
        _localRunner = localRunner;
        _directRunner = directRunner;
        _pooledRunner = pooledRunner;
    }

    public IJobRunner Select(JobDefinition job)
    {
        if (!string.IsNullOrWhiteSpace(job.Agent)) return _directRunner;
        if (job.Remote) return _pooledRunner;
        return _localRunner;
    }
}
