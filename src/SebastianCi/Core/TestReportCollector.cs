using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの reports に指定されたグロブパターンでワークスペースから JUnit XML を探し、
/// 解析・集計してサマリーを返す処理だけを担当する。
/// </summary>
public sealed class TestReportCollector
{
    private readonly string _workspacePath;

    public TestReportCollector(string workspacePath) => _workspacePath = Path.GetFullPath(workspacePath);

    /// <summary>
    /// レポートを収集して集計する。reports 未指定なら null。
    /// パターンに一致するファイルが1つも無い場合は警告を出して null を返す（ジョブの成否には影響しない）。
    /// </summary>
    public async Task<TestReportSummary?> CollectAsync(
        string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        if (job.Reports.Count == 0) return null;

        List<string> reportFilePaths = FindReportFiles(job.Reports);
        if (reportFilePaths.Count == 0)
        {
            ConsoleLogger.WriteWarning(
                $"⚠ ジョブ '{jobId}' の reports に一致するファイルが見つかりませんでした: {string.Join(", ", job.Reports)}");
            return null;
        }

        List<TestReportSummary> summaries = new(reportFilePaths.Count);
        foreach (string reportFilePath in reportFilePaths)
        {
            summaries.Add(await ParseFileAsync(jobId, reportFilePath, cancellationToken));
        }

        return JUnitReportParser.Aggregate(summaries);
    }

    private List<string> FindReportFiles(IReadOnlyList<string> patterns)
    {
        Matcher matcher = new();
        matcher.AddIncludePatterns(patterns);
        PatternMatchingResult result = matcher.Execute(
            new DirectoryInfoWrapper(new DirectoryInfo(_workspacePath)));
        return result.Files
            .Select(file => Path.Combine(_workspacePath, file.Path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<TestReportSummary> ParseFileAsync(
        string jobId, string reportFilePath, CancellationToken cancellationToken)
    {
        try
        {
            string xmlContent = await File.ReadAllTextAsync(reportFilePath, cancellationToken);
            return JUnitReportParser.Parse(xmlContent);
        }
        catch (Exception exception) when (exception is IOException or InvalidPipelineException)
        {
            ConsoleLogger.WriteWarning(
                $"⚠ ジョブ '{jobId}' のレポート '{reportFilePath}' を読み取れませんでした: {exception.Message}");
            return new TestReportSummary(0, 0, 0, 0, []);
        }
    }
}
