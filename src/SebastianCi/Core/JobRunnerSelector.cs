using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの指定に応じて実行先を選ぶだけを担当する。
/// runner（プラグイン）→ プラグイン実行、agent（明示URL）→ 直接委譲、
/// remote → プール割り当て、いずれも無し → ローカル実行。
/// </summary>
public sealed class JobRunnerSelector
{
    private readonly IJobRunner _localRunner;
    private readonly IJobRunner _directRunner;
    private readonly IJobRunner _pooledRunner;
    private readonly IReadOnlyDictionary<string, IJobRunner> _pluginRunners;

    public JobRunnerSelector(
        IJobRunner localRunner, IJobRunner directRunner, IJobRunner pooledRunner,
        IReadOnlyDictionary<string, IJobRunner> pluginRunners)
    {
        _localRunner = localRunner;
        _directRunner = directRunner;
        _pooledRunner = pooledRunner;
        _pluginRunners = pluginRunners;
    }

    public IJobRunner Select(JobDefinition job)
    {
        if (!string.IsNullOrWhiteSpace(job.Runner)) return SelectPluginRunner(job.Runner);
        if (!string.IsNullOrWhiteSpace(job.Agent)) return _directRunner;
        if (job.Remote) return _pooledRunner;
        return _localRunner;
    }

    private IJobRunner SelectPluginRunner(string runnerName)
        => _pluginRunners.TryGetValue(runnerName, out IJobRunner? runner)
            ? runner
            : throw new InvalidPipelineException(
                $"runner '{runnerName}' に対応するプラグインが読み込まれていません（plugins の指定を確認してください）。");
}
