using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// 1ジョブを実行する責務を表す。ローカルのコンテナ実行と、リモートエージェントへの
/// 委譲を同じインターフェースで扱えるようにする。失敗時は例外をスローする。
/// </summary>
public interface IJobRunner
{
    Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default);
}
