using SebastianCi.Core;
using SebastianCi.Models;
using static SebastianCi.Tests.TestPipelineFactory;

namespace SebastianCi.Tests;

public class ChangeDetectorTests
{
    [Fact]
    public void ShouldRun_ReturnsTrueWhenJobHasNoChangesFilter()
    {
        ChangeDetector detector = new(["docs/readme.md"]);

        Assert.True(detector.ShouldRun(Job()));
    }

    [Fact]
    public void ShouldRun_ReturnsTrueWhenDiffIsUnknown()
    {
        ChangeDetector detector = new(null);
        JobDefinition job = Job();
        job.Changes = ["src/**"];

        Assert.True(detector.ShouldRun(job));
    }

    [Fact]
    public void ShouldRun_ReturnsTrueWhenChangedFileMatchesPattern()
    {
        ChangeDetector detector = new(["src/main.cs", "docs/readme.md"]);
        JobDefinition job = Job();
        job.Changes = ["src/**"];

        Assert.True(detector.ShouldRun(job));
    }

    [Fact]
    public void ShouldRun_ReturnsFalseWhenNoChangedFileMatchesPattern()
    {
        ChangeDetector detector = new(["docs/readme.md"]);
        JobDefinition job = Job();
        job.Changes = ["src/**", "*.csproj"];

        Assert.False(detector.ShouldRun(job));
    }
}
