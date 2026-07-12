using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの agent 指定に応じて、ローカル実行かリモートエージェント実行かを選ぶだけを担当する。
/// </summary>
public sealed class JobRunnerSelector
{
    private readonly IJobRunner _localRunner;
    private readonly IJobRunner _remoteRunner;

    public JobRunnerSelector(IJobRunner localRunner, IJobRunner remoteRunner)
    {
        _localRunner = localRunner;
        _remoteRunner = remoteRunner;
    }

    public IJobRunner Select(JobDefinition job)
        => string.IsNullOrWhiteSpace(job.Agent) ? _localRunner : _remoteRunner;
}
