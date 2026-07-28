using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public sealed class JUnitReportParserTests
{
    [Fact]
    public void Parse_CountsResultsFromTestsuites()
    {
        TestReportSummary summary = JUnitReportParser.Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites>
              <testsuite name="SuiteA" tests="3">
                <testcase classname="Suite.A" name="Passes" time="0.01" />
                <testcase classname="Suite.A" name="Fails" time="0.02">
                  <failure message="expected 1 but got 2">stack trace here</failure>
                </testcase>
                <testcase classname="Suite.A" name="IsSkipped">
                  <skipped />
                </testcase>
              </testsuite>
              <testsuite name="SuiteB" tests="1">
                <testcase classname="Suite.B" name="Errors">
                  <error message="null reference" />
                </testcase>
              </testsuite>
            </testsuites>
            """);

        Assert.Equal(4, summary.Total);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Passed);
        Assert.True(summary.HasFailures);
        Assert.Equal(2, summary.FailedTests.Count);
        Assert.Contains(summary.FailedTests,
            failed => failed.Name == "Fails" && failed.Message == "expected 1 but got 2");
        Assert.Contains(summary.FailedTests,
            failed => failed.Name == "Errors" && failed.ClassName == "Suite.B");
    }

    [Fact]
    public void Parse_UsesBodyFirstLineWhenMessageAttributeMissing()
    {
        TestReportSummary summary = JUnitReportParser.Parse("""
            <testsuite tests="1">
              <testcase name="Fails">
                <failure>Assert.Equal() Failure
            more detail</failure>
              </testcase>
            </testsuite>
            """);

        Assert.Equal("Assert.Equal() Failure", summary.FailedTests.Single().Message);
    }

    [Fact]
    public void Parse_ThrowsOnBrokenXml()
        => Assert.Throws<InvalidPipelineException>(() => JUnitReportParser.Parse("<testsuite"));

    [Fact]
    public void Aggregate_SumsCountsAndConcatenatesFailures()
    {
        TestReportSummary first = new(2, 1, 0, 0, [new FailedTestCase("a", "C", "m")]);
        TestReportSummary second = new(3, 0, 1, 1, [new FailedTestCase("b", "C", "m")]);

        TestReportSummary total = JUnitReportParser.Aggregate([first, second]);

        Assert.Equal(5, total.Total);
        Assert.Equal(1, total.Failures);
        Assert.Equal(1, total.Errors);
        Assert.Equal(1, total.Skipped);
        Assert.Equal(2, total.FailedTests.Count);
    }
}

public sealed class TestReportCollectorTests : IDisposable
{
    private readonly string _workspacePath;

    public TestReportCollectorTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"sebastian-ci-reports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workspacePath, "results"));
    }

    public void Dispose() => Directory.Delete(_workspacePath, recursive: true);

    [Fact]
    public async Task CollectAsync_AggregatesMatchingReportFiles()
    {
        WriteReport("results/one.xml", passed: 2, failed: 1);
        WriteReport("results/two.xml", passed: 1, failed: 0);
        File.WriteAllText(Path.Combine(_workspacePath, "results", "ignored.txt"), "not xml");
        TestReportCollector collector = new(_workspacePath);
        JobDefinition job = new() { Reports = ["results/*.xml"] };

        TestReportSummary? summary = await collector.CollectAsync("test", job);

        Assert.NotNull(summary);
        Assert.Equal(4, summary.Total);
        Assert.Equal(1, summary.Failures);
        Assert.Equal(3, summary.Passed);
    }

    [Fact]
    public async Task CollectAsync_ReturnsNullWithoutReportsConfig()
    {
        TestReportCollector collector = new(_workspacePath);

        Assert.Null(await collector.CollectAsync("test", new JobDefinition()));
    }

    [Fact]
    public async Task CollectAsync_ReturnsNullWhenNothingMatches()
    {
        TestReportCollector collector = new(_workspacePath);
        JobDefinition job = new() { Reports = ["missing/**/*.xml"] };

        Assert.Null(await collector.CollectAsync("test", job));
    }

    private void WriteReport(string relativePath, int passed, int failed)
    {
        IEnumerable<string> passing = Enumerable.Range(0, passed)
            .Select(index => $"""<testcase classname="C" name="pass{index}" />""");
        IEnumerable<string> failing = Enumerable.Range(0, failed)
            .Select(index => $"""<testcase classname="C" name="fail{index}"><failure message="boom" /></testcase>""");
        string xml = $"""
            <testsuite tests="{passed + failed}">
            {string.Join('\n', passing)}
            {string.Join('\n', failing)}
            </testsuite>
            """;
        File.WriteAllText(Path.Combine(_workspacePath, relativePath), xml);
    }
}
