using System.Xml.Linq;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// JUnit XML 形式（&lt;testsuites&gt; / &lt;testsuite&gt;）のテストレポートの解析・集計だけを担当する。
/// dotnet test（JunitXml ロガー）・pytest・Jest・Gradle など、JUnit 形式を出力する
/// あらゆるテストランナーの結果を扱える。
/// </summary>
public static class JUnitReportParser
{
    private const int MessageMaxLength = 300;

    /// <summary>複数レポートファイルの内容を1つのサマリーへ集計する。</summary>
    public static TestReportSummary Aggregate(IEnumerable<TestReportSummary> summaries)
        => summaries.Aggregate(
            new TestReportSummary(0, 0, 0, 0, []),
            (total, next) => new TestReportSummary(
                total.Total + next.Total,
                total.Failures + next.Failures,
                total.Errors + next.Errors,
                total.Skipped + next.Skipped,
                [.. total.FailedTests, .. next.FailedTests]));

    /// <summary>JUnit XML 文字列を解析する。形式が不正な場合は InvalidPipelineException をスローする。</summary>
    public static TestReportSummary Parse(string xmlContent)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xmlContent);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidPipelineException($"JUnit レポートのXML解析に失敗しました: {exception.Message}");
        }

        List<XElement> testCases = document.Descendants("testcase").ToList();
        List<FailedTestCase> failedTests = testCases
            .Select(ToFailedTestCase)
            .Where(failed => failed is not null)
            .Select(failed => failed!)
            .ToList();

        return new TestReportSummary(
            Total: testCases.Count,
            Failures: testCases.Count(testCase => testCase.Element("failure") is not null),
            Errors: testCases.Count(testCase => testCase.Element("error") is not null),
            Skipped: testCases.Count(testCase => testCase.Element("skipped") is not null),
            FailedTests: failedTests);
    }

    private static FailedTestCase? ToFailedTestCase(XElement testCase)
    {
        XElement? problem = testCase.Element("failure") ?? testCase.Element("error");
        if (problem is null) return null;

        return new FailedTestCase(
            Name: testCase.Attribute("name")?.Value ?? "(名称不明)",
            ClassName: testCase.Attribute("classname")?.Value ?? "",
            Message: ExtractMessage(problem));
    }

    private static string ExtractMessage(XElement problem)
    {
        string message = problem.Attribute("message")?.Value is { Length: > 0 } attributeMessage
            ? attributeMessage
            : problem.Value.Trim().Split('\n').FirstOrDefault() ?? "";
        return message.Length > MessageMaxLength ? message[..MessageMaxLength] + "…" : message;
    }
}
