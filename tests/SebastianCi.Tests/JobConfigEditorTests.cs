using SebastianCi.Core;
using SebastianCi.Web;

namespace SebastianCi.Tests;

public class JobConfigEditorTests
{
    private const string BaseYaml = """
        # 先頭のコメントは保持される
        name: sample
        image: alpine:3.20
        stages: [prepare, build]

        jobs:
          restore:
            stage: prepare
            script:
              - dotnet restore
          test:
            stage: build
            needs: [restore]
            timeout: 300
            script:
              - dotnet test
        """;

    private static JobFormPayload Payload(
        string name, string? originalName = null, string? image = null, string? stage = null,
        List<string>? needs = null, List<string>? script = null, Dictionary<string, string>? env = null,
        int timeout = 0, int retry = 0, bool continueOnError = false, bool shell = false)
        => new(originalName, name, image, stage, needs, script ?? ["echo hi"], env, null,
            timeout, retry, continueOnError, shell);

    [Fact]
    public void Read_ReturnsStagesAndJobSummaries()
    {
        JobsSnapshot snapshot = JobConfigEditor.Read(BaseYaml);

        Assert.Equal(["prepare", "build"], snapshot.Stages);
        Assert.Equal(2, snapshot.Jobs.Count);
        JobSummary test = snapshot.Jobs.Single(job => job.Name == "test");
        Assert.Equal("build", test.Stage);
        Assert.Equal(["restore"], test.Needs);
        Assert.Equal(["dotnet test"], test.Script);
        Assert.Equal(300, test.Timeout);
    }

    [Fact]
    public void Read_ListsNonFormKeysAsAdvanced()
    {
        const string yaml = """
            jobs:
              build:
                matrix:
                  configuration: [Debug, Release]
                cache:
                  - /root/.nuget/packages
                script: [dotnet build]
            """;

        JobsSnapshot snapshot = JobConfigEditor.Read(yaml);

        Assert.Equal(["matrix", "cache"], snapshot.Jobs.Single().AdvancedKeys);
    }

    [Fact]
    public void Upsert_AddsJobToExistingSectionAndKeepsComments()
    {
        string result = JobConfigEditor.Upsert(
            BaseYaml, Payload("lint", stage: "prepare", image: "alpine:3.20", script: ["echo lint"]));

        Assert.Contains("# 先頭のコメントは保持される", result);
        Assert.Contains("  lint:", result);
        Assert.Contains("    image: alpine:3.20", result);
        Assert.Contains("    stage: prepare", result);
        Assert.Contains("    - echo lint", result);
        Assert.Equal(3, JobConfigEditor.Read(result).Jobs.Count);
    }

    [Fact]
    public void Upsert_CreatesJobsSectionWhenYamlIsEmpty()
    {
        string result = JobConfigEditor.Upsert("", Payload("hello", image: "alpine:3.20"));

        JobsSnapshot snapshot = JobConfigEditor.Read(result);
        JobSummary job = snapshot.Jobs.Single();
        Assert.Equal("hello", job.Name);
        Assert.Equal("alpine:3.20", job.Image);
        Assert.Equal(["echo hi"], job.Script);
    }

    [Fact]
    public void Upsert_ReplacesExistingJobBlock()
    {
        string result = JobConfigEditor.Upsert(
            BaseYaml, Payload("test", originalName: "test", stage: "build",
                needs: ["restore"], script: ["dotnet test --no-build"], timeout: 600));

        JobSummary job = JobConfigEditor.Read(result).Jobs.Single(j => j.Name == "test");
        Assert.Equal(["dotnet test --no-build"], job.Script);
        Assert.Equal(600, job.Timeout);
        Assert.DoesNotContain("timeout: 300", result);
        Assert.Contains("  restore:", result);
    }

    [Fact]
    public void Upsert_PreservesAdvancedKeysWhenEditing()
    {
        const string yaml = """
            jobs:
              build:
                matrix:
                  configuration: [Debug, Release]
                script: [dotnet build]
            """;

        string result = JobConfigEditor.Upsert(
            yaml, Payload("build", originalName: "build", script: ["dotnet build --nologo"]));

        Assert.Contains("matrix:", result);
        Assert.Contains("Debug", result);
        Assert.Contains("Release", result);
        Assert.Equal(["dotnet build --nologo"], JobConfigEditor.Read(result).Jobs.Single().Script);
    }

    [Fact]
    public void Upsert_RenameUpdatesNeedsReferences()
    {
        string result = JobConfigEditor.Upsert(
            BaseYaml, Payload("prepare-deps", originalName: "restore", stage: "prepare",
                script: ["dotnet restore"]));

        JobsSnapshot snapshot = JobConfigEditor.Read(result);
        Assert.Contains(snapshot.Jobs, job => job.Name == "prepare-deps");
        Assert.DoesNotContain(snapshot.Jobs, job => job.Name == "restore");
        Assert.Equal(["prepare-deps"], snapshot.Jobs.Single(job => job.Name == "test").Needs);
    }

    [Fact]
    public void Upsert_RenameUpdatesBlockStyleNeeds()
    {
        const string yaml = """
            jobs:
              a:
                script: [echo a]
              b:
                needs:
                  - a
                script: [echo b]
            """;

        string result = JobConfigEditor.Upsert(yaml, Payload("first", originalName: "a", script: ["echo a"]));

        Assert.Equal(["first"], JobConfigEditor.Read(result).Jobs.Single(job => job.Name == "b").Needs);
    }

    [Fact]
    public void Upsert_ThrowsOnInlineJobsStyle()
    {
        const string yaml = """jobs: { build: { script: [echo hi] } }""";

        Assert.Throws<InvalidPipelineException>(() => JobConfigEditor.Upsert(yaml, Payload("x")));
    }

    [Fact]
    public void Upsert_WritesEnvAndFlags()
    {
        string result = JobConfigEditor.Upsert("", Payload(
            "deploy", env: new() { ["KEY"] = "value" }, retry: 2, continueOnError: true, shell: true));

        JobSummary job = JobConfigEditor.Read(result).Jobs.Single();
        Assert.Equal("value", job.Env["KEY"]);
        Assert.Equal(2, job.Retry);
        Assert.True(job.ContinueOnError);
        Assert.True(job.Shell);
        Assert.DoesNotContain("timeout:", result);
        Assert.DoesNotContain("image:", result);
    }

    [Fact]
    public void Remove_DeletesOnlyTargetJob()
    {
        string result = JobConfigEditor.Remove(BaseYaml, "test");

        JobsSnapshot snapshot = JobConfigEditor.Read(result);
        Assert.Single(snapshot.Jobs);
        Assert.Equal("restore", snapshot.Jobs.Single().Name);
        Assert.Contains("# 先頭のコメントは保持される", result);
    }

    [Fact]
    public void Remove_ReturnsUnchangedYamlWhenJobMissing()
    {
        string result = JobConfigEditor.Remove(BaseYaml, "unknown");

        Assert.Equal(BaseYaml.Replace("\r\n", "\n"), result);
    }
}
