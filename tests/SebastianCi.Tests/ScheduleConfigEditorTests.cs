using SebastianCi.Web;

namespace SebastianCi.Tests;

public sealed class ScheduleConfigEditorTests
{
    private const string BaseYaml = """
        # コメントは保持される
        name: sample
        image: alpine

        schedules:
          nightly:
            cron: 0 3 * * *
            job: build
            params:
              env: production
            yes: true

        jobs:
          build:
            script: [echo hi]
        """;

    [Fact]
    public void Read_ReturnsScheduleSummaries()
    {
        List<ScheduleSummary> schedules = ScheduleConfigEditor.Read(BaseYaml);

        ScheduleSummary schedule = schedules.Single();
        Assert.Equal("nightly", schedule.Id);
        Assert.Equal("0 3 * * *", schedule.Cron);
        Assert.Equal("build", schedule.Job);
        Assert.Equal("production", schedule.Params["env"]);
        Assert.True(schedule.Yes);
        Assert.False(schedule.Rebuild);
        Assert.True(schedule.Enabled);
    }

    [Fact]
    public void Read_EnabledDefaultsToTrueWhenOmitted()
    {
        const string yaml = """
            schedules:
              nightly:
                cron: 0 3 * * *
            """;

        Assert.True(ScheduleConfigEditor.Read(yaml).Single().Enabled);
    }

    [Fact]
    public void Read_ExplicitlyDisabledScheduleIsRead()
    {
        const string yaml = """
            schedules:
              nightly:
                cron: 0 3 * * *
                enabled: false
            """;

        Assert.False(ScheduleConfigEditor.Read(yaml).Single().Enabled);
    }

    [Fact]
    public void Upsert_AddsScheduleAndKeepsComments()
    {
        ScheduleFormPayload payload = new(null, "weekly", "0 0 * * 1", null, null, false, false, true);

        string result = ScheduleConfigEditor.Upsert(BaseYaml, payload);

        Assert.Contains("# コメントは保持される", result);
        List<ScheduleSummary> schedules = ScheduleConfigEditor.Read(result);
        Assert.Equal(2, schedules.Count);
        ScheduleSummary added = schedules.Single(schedule => schedule.Id == "weekly");
        Assert.Equal("0 0 * * 1", added.Cron);
        Assert.Null(added.Job);
    }

    [Fact]
    public void Upsert_CreatesSectionWhenMissing()
    {
        ScheduleFormPayload payload = new(null, "nightly", "0 3 * * *", "build", null, false, false, true);

        string result = ScheduleConfigEditor.Upsert(
            "image: alpine\njobs:\n  build:\n    script: [echo hi]", payload);

        ScheduleSummary schedule = ScheduleConfigEditor.Read(result).Single();
        Assert.Equal("nightly", schedule.Id);
        Assert.Equal("build", schedule.Job);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Upsert_ReplacesExistingSchedule()
    {
        ScheduleFormPayload payload = new(
            "nightly", "nightly", "0 4 * * *", null, null, false, true, false);

        string result = ScheduleConfigEditor.Upsert(BaseYaml, payload);

        ScheduleSummary schedule = ScheduleConfigEditor.Read(result).Single();
        Assert.Equal("0 4 * * *", schedule.Cron);
        Assert.Null(schedule.Job);
        Assert.True(schedule.Rebuild);
        Assert.False(schedule.Enabled);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Remove_DeletesScheduleAndSectionWhenLast()
    {
        string result = ScheduleConfigEditor.Remove(BaseYaml, "nightly");

        Assert.Empty(ScheduleConfigEditor.Read(result));
        Assert.DoesNotContain("schedules:", result);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Remove_KeepsSectionWhenOthersRemain()
    {
        string yaml = ScheduleConfigEditor.Upsert(
            BaseYaml, new ScheduleFormPayload(null, "weekly", "0 0 * * 1", null, null, false, false, true));

        string result = ScheduleConfigEditor.Remove(yaml, "weekly");

        Assert.Contains("schedules:", result);
        Assert.Equal("nightly", ScheduleConfigEditor.Read(result).Single().Id);
    }

    [Fact]
    public void Remove_UnknownIdReturnsYamlUnchanged()
        => Assert.Equal(BaseYaml, ScheduleConfigEditor.Remove(BaseYaml, "missing"));
}
