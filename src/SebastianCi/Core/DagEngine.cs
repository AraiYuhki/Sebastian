using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの実効依存関係（needs ＋ ステージ順序）に基づく並列実行の制御だけを担当する。
/// </summary>
public sealed class DagEngine
{
    private readonly JobRunnerSelector _runnerSelector;
    private readonly ArtifactManager _artifactManager;
    private readonly ChangeDetector _changeDetector;
    private readonly IApprovalGate _approvalGate;
    private readonly TestReportCollector? _testReportCollector;
    private readonly int? _maxParallel;

    public DagEngine(
        JobRunnerSelector runnerSelector, ArtifactManager artifactManager,
        ChangeDetector changeDetector, int? maxParallel = null, IApprovalGate? approvalGate = null,
        TestReportCollector? testReportCollector = null)
    {
        _runnerSelector = runnerSelector;
        _artifactManager = artifactManager;
        _changeDetector = changeDetector;
        _approvalGate = approvalGate ?? new ConsoleApprovalGate(autoApprove: false);
        _testReportCollector = testReportCollector;
        _maxParallel = maxParallel;
    }

    /// <summary>
    /// 依存関係を解決しながら全ジョブを実行する。
    /// 依存のないジョブ同士は Task として同時に走り、Task.WhenAll で合流する。
    /// --max-parallel が指定された場合は、同時に走るコンテナ数をセマフォで制限する。
    /// </summary>
    public async Task<IReadOnlyList<JobResult>> ExecuteAsync(
        PipelineDefinition pipeline, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveNeeds =
            DependencyGraph.BuildEffectiveNeeds(pipeline);
        List<string> sortedJobIds = DependencyGraph.SortTopologically(effectiveNeeds);
        ConsoleLogger.WriteInfo($"🚀 パイプライン '{pipeline.Name}' を開始します (ジョブ数: {sortedJobIds.Count})");

        using SemaphoreSlim? throttle = _maxParallel is int limit ? new SemaphoreSlim(limit, limit) : null;
        Dictionary<string, Task<JobResult>> jobTasks = new();
        foreach (string jobId in sortedJobIds)
        {
            Task<JobResult>[] dependencyTasks =
                effectiveNeeds[jobId].Select(needId => jobTasks[needId]).ToArray();
            jobTasks[jobId] = ExecuteJobAsync(jobId, pipeline.Jobs[jobId], dependencyTasks, throttle, cancellationToken);
        }

        return await Task.WhenAll(jobTasks.Values);
    }

    private async Task<JobResult> ExecuteJobAsync(
        string jobId, JobDefinition job, Task<JobResult>[] dependencyTasks,
        SemaphoreSlim? throttle, CancellationToken cancellationToken)
    {
        JobResult[] dependencyResults = await Task.WhenAll(dependencyTasks);
        if (dependencyResults.Any(result => !IsDependencySatisfied(result.Status)))
        {
            ConsoleLogger.WriteWarning($"⏭  ジョブ '{jobId}' は先行ジョブの失敗によりスキップされました。");
            return new JobResult(jobId, JobStatus.Skipped, TimeSpan.Zero);
        }

        if (!_changeDetector.ShouldRun(job))
        {
            ConsoleLogger.WriteWarning($"⏭  ジョブ '{jobId}' は changes に一致する変更がないためスキップされました。");
            return new JobResult(jobId, JobStatus.SkippedByChanges, TimeSpan.Zero);
        }

        if (!await IsApprovedAsync(jobId, job, cancellationToken))
        {
            ConsoleLogger.WriteError($"❌ ジョブ '{jobId}' は承認されなかったため中止しました。");
            return new JobResult(jobId, JobStatus.Failed, TimeSpan.Zero);
        }

        return await RunThrottledAsync(jobId, job, throttle, cancellationToken);
    }

    /// <summary>approval 指定ジョブは、依存が満たされ変更検知も通ったあと・実行の直前に承認を確認する。</summary>
    private async Task<bool> IsApprovedAsync(string jobId, JobDefinition job, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(job.Approval)
            || await _approvalGate.RequestAsync(jobId, job.Approval, cancellationToken);

    /// <summary>
    /// 後続ジョブの実行を許すかどうか。変更なしスキップは「実行不要だった」だけ、
    /// FailedIgnored は continue-on-error による許容失敗であり、いずれも後続を妨げない。
    /// </summary>
    private static bool IsDependencySatisfied(JobStatus status)
        => status is JobStatus.Success or JobStatus.SkippedByChanges or JobStatus.FailedIgnored;

    /// <summary>並列度の枠を確保してからジョブを実行する。枠の確保待ちは「開始」表示より前に行う。</summary>
    private async Task<JobResult> RunThrottledAsync(
        string jobId, JobDefinition job, SemaphoreSlim? throttle, CancellationToken cancellationToken)
    {
        if (throttle is not null) await throttle.WaitAsync(cancellationToken);
        try
        {
            return await RunSingleJobAsync(jobId, job, cancellationToken);
        }
        finally
        {
            throttle?.Release();
        }
    }

    private async Task<JobResult> RunSingleJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        string executionLabel = job.Shell ? "ホストで直接実行" : $"イメージ: {job.Image}";
        ConsoleLogger.WriteInfo($"▶ ジョブ '{jobId}' を開始します ({executionLabel})");
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            await RunWithRetriesAsync(jobId, job, cancellationToken);
            await _artifactManager.CollectAsync(jobId, job, cancellationToken);
            TestReportSummary? tests = await CollectTestReportsAsync(jobId, job, cancellationToken);
            stopwatch.Stop();
            ConsoleLogger.WriteSuccess($"✅ ジョブ '{jobId}' が成功しました ({stopwatch.Elapsed.TotalSeconds:F1} 秒)");
            return new JobResult(jobId, JobStatus.Success, stopwatch.Elapsed, tests);
        }
        catch (ContainerExecutionException exception)
        {
            // 失敗したジョブこそ「どのテストが落ちたか」が重要なため、レポートは失敗時も回収する
            TestReportSummary? tests = await CollectTestReportsAsync(jobId, job, cancellationToken);
            stopwatch.Stop();
            return HandleFailure(jobId, job, exception, stopwatch.Elapsed) with { Tests = tests };
        }
    }

    /// <summary>reports 指定ジョブのテストレポートを回収・集計し、サマリーを表示する。</summary>
    private async Task<TestReportSummary?> CollectTestReportsAsync(
        string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        if (_testReportCollector is null || job.Reports.Count == 0) return null;

        TestReportSummary? summary = await _testReportCollector.CollectAsync(jobId, job, cancellationToken);
        if (summary is not null) ReportTestSummary(jobId, summary);
        return summary;
    }

    private static void ReportTestSummary(string jobId, TestReportSummary summary)
    {
        string counts =
            $"成功 {summary.Passed} / 失敗 {summary.Failures} / エラー {summary.Errors}"
            + $" / スキップ {summary.Skipped} (全 {summary.Total} 件)";
        if (!summary.HasFailures)
        {
            ConsoleLogger.WriteSuccess($"🧪 ジョブ '{jobId}' のテスト結果: {counts}");
            return;
        }

        ConsoleLogger.WriteError($"🧪 ジョブ '{jobId}' のテスト結果: {counts}");
        const int maxListedFailures = 10;
        foreach (FailedTestCase failed in summary.FailedTests.Take(maxListedFailures))
        {
            string qualifiedName = string.IsNullOrEmpty(failed.ClassName)
                ? failed.Name
                : $"{failed.ClassName}.{failed.Name}";
            ConsoleLogger.WriteError($"   ✗ {qualifiedName}: {failed.Message}");
        }

        if (summary.FailedTests.Count > maxListedFailures)
        {
            ConsoleLogger.WriteError($"   … ほか {summary.FailedTests.Count - maxListedFailures} 件");
        }
    }

    /// <summary>ジョブを実行し、失敗した場合は retry で指定された回数まで再実行する。</summary>
    private async Task RunWithRetriesAsync(string jobId, JobDefinition job, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await _runnerSelector.Select(job).RunJobAsync(jobId, job, cancellationToken);
                return;
            }
            catch (ContainerExecutionException) when (attempt < job.Retry)
            {
                ConsoleLogger.WriteWarning($"🔁 ジョブ '{jobId}' が失敗しました。再試行します ({attempt + 1}/{job.Retry})");
            }
        }
    }

    /// <summary>失敗ジョブの最終結果を決める。continue-on-error 指定なら許容失敗として扱う。</summary>
    private static JobResult HandleFailure(string jobId, JobDefinition job, Exception exception, TimeSpan elapsed)
    {
        if (job.ContinueOnError)
        {
            ConsoleLogger.WriteWarning(
                $"⚠ ジョブ '{jobId}' は失敗しましたが continue-on-error のため続行します: {exception.Message}");
            return new JobResult(jobId, JobStatus.FailedIgnored, elapsed);
        }

        ConsoleLogger.WriteError($"❌ ジョブ '{jobId}' が失敗しました: {exception.Message}");
        return new JobResult(jobId, JobStatus.Failed, elapsed);
    }
}
