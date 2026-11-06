using SebastianCi.Models;

namespace SebastianCi.Web;

/// <summary>ミニマルAPIが「どのリポジトリに対する要求か」を解決するための窓口。</summary>
public interface IRepositoryResolver
{
    /// <summary>id に対応するリポジトリ。単体 `serve` では id を無視して唯一のリポジトリを返す。無ければ null。</summary>
    RepositoryApi? Resolve(string? id);
}

/// <summary>単体 `serve` 用。常に同じ1リポジトリを返す。</summary>
public sealed class SingleRepositoryResolver(RepositoryApi repository) : IRepositoryResolver
{
    public RepositoryApi? Resolve(string? id) => repository;
}

/// <summary>ハブの一覧に出す、直近1回分の実行結果。</summary>
public sealed record RunSummary(
    string CommitHash, bool IsSuccess, DateTimeOffset ExecutedAt, double DurationSeconds, List<string> FailedJobs);

/// <summary>ハブの「全ジョブ」一覧の1行（リポジトリ内のジョブ1件と、直近実行での状態）。</summary>
public sealed record JobOverview(string Name, string Stage, JobStatus? LastStatus);

/// <summary>ハブの一覧用に、1リポジトリの現在の状態をまとめたもの。</summary>
public sealed record RepositorySummary(
    bool ConfigExists,
    string? ConfigError,
    bool Running,
    List<JobOverview> Jobs,
    int ScheduleCount,
    DateTime? NextRunAt,
    RunSummary? LastRun,
    List<RunSummary> RecentRuns);
