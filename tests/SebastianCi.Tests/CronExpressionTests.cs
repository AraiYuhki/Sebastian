using SebastianCi.Core;

namespace SebastianCi.Tests;

public sealed class CronExpressionTests
{
    [Fact]
    public void TryParse_WrongFieldCountFails()
    {
        Assert.False(CronExpression.TryParse("0 3 * *", out CronExpression? cron, out string? error));
        Assert.Null(cron);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("60 * * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 0 32 * *")]
    [InlineData("0 0 * 13 *")]
    [InlineData("0 0 * * 8")]
    [InlineData("0 0 * * *,foo")]
    public void TryParse_OutOfRangeOrInvalidValueFails(string expression)
        => Assert.False(CronExpression.TryParse(expression, out _, out _));

    [Fact]
    public void Matches_ExactValueMatchesOnlyThatMoment()
    {
        Assert.True(CronExpression.TryParse("0 3 * * *", out CronExpression? cron, out _));

        Assert.True(cron!.Matches(new DateTime(2026, 1, 1, 3, 0, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 3, 1, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 4, 0, 0)));
    }

    [Fact]
    public void Matches_StepSupportsEveryNMinutes()
    {
        Assert.True(CronExpression.TryParse("*/15 * * * *", out CronExpression? cron, out _));

        Assert.True(cron!.Matches(new DateTime(2026, 1, 1, 10, 0, 0)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 10, 15, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 10, 20, 0)));
    }

    [Fact]
    public void Matches_RangeAndListAreSupported()
    {
        Assert.True(CronExpression.TryParse("0 9-17 * * 1-5", out CronExpression? cron, out _));

        // 2026-01-05 は月曜日
        Assert.True(cron!.Matches(new DateTime(2026, 1, 5, 9, 0, 0)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 5, 17, 0, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 5, 18, 0, 0)));
        // 2026-01-04 は日曜日
        Assert.False(cron.Matches(new DateTime(2026, 1, 4, 9, 0, 0)));
    }

    [Fact]
    public void Matches_SundayCanBeSpecifiedAs0Or7()
    {
        Assert.True(CronExpression.TryParse("0 0 * * 7", out CronExpression? cron, out _));

        // 2026-01-04 は日曜日
        Assert.True(cron!.Matches(new DateTime(2026, 1, 4, 0, 0, 0)));
    }

    [Fact]
    public void GetNextOccurrence_FindsFollowingMatchingMinute()
    {
        Assert.True(CronExpression.TryParse("30 3 * * *", out CronExpression? cron, out _));

        DateTime? next = cron!.GetNextOccurrence(new DateTime(2026, 1, 1, 3, 30, 0));

        Assert.Equal(new DateTime(2026, 1, 2, 3, 30, 0), next);
    }
}
