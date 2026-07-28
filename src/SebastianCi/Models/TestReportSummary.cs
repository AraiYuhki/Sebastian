namespace SebastianCi.Models;

/// <summary>
/// JUnit 形式のテストレポートを集計した結果。
/// </summary>
public sealed record TestReportSummary(
    int Total, int Failures, int Errors, int Skipped, List<FailedTestCase> FailedTests)
{
    public int Passed => Total - Failures - Errors - Skipped;

    public bool HasFailures => Failures > 0 || Errors > 0;
}

/// <summary>失敗（またはエラー）したテストケース1件分の情報。</summary>
public sealed record FailedTestCase(string Name, string ClassName, string Message);
