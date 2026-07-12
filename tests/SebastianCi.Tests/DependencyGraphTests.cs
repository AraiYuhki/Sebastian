using SebastianCi.Core;
using SebastianCi.Models;
using static SebastianCi.Tests.TestPipelineFactory;

namespace SebastianCi.Tests;

public class DependencyGraphTests
{
    [Fact]
    public void SortTopologically_OrdersDependenciesBeforeDependents()
    {
        Dictionary<string, IReadOnlyList<string>> needs = new()
        {
            ["build"] = ["restore"],
            ["restore"] = [],
            ["test"] = ["build"]
        };

        List<string> sorted = DependencyGraph.SortTopologically(needs);

        Assert.True(sorted.IndexOf("restore") < sorted.IndexOf("build"));
        Assert.True(sorted.IndexOf("build") < sorted.IndexOf("test"));
    }

    [Fact]
    public void SortTopologically_ThrowsOnCycle()
    {
        Dictionary<string, IReadOnlyList<string>> needs = new()
        {
            ["a"] = ["b"],
            ["b"] = ["a"]
        };

        InvalidPipelineException exception =
            Assert.Throws<InvalidPipelineException>(() => DependencyGraph.SortTopologically(needs));
        Assert.Contains("循環参照", exception.Message);
    }

    [Fact]
    public void BuildEffectiveNeeds_AddsImplicitDependencyOnEarlierStages()
    {
        PipelineDefinition pipeline = Pipeline(
            ("prepareJob", Job(stage: "prepare")),
            ("buildJob", Job(stage: "build")));
        pipeline.Stages = ["prepare", "build"];

        IReadOnlyDictionary<string, IReadOnlyList<string>> effective =
            DependencyGraph.BuildEffectiveNeeds(pipeline);

        Assert.Contains("prepareJob", effective["buildJob"]);
        Assert.Empty(effective["prepareJob"]);
    }

    [Fact]
    public void BuildEffectiveNeeds_WithoutStages_KeepsExplicitNeedsOnly()
    {
        PipelineDefinition pipeline = Pipeline(
            ("a", Job()),
            ("b", Job(needs: ["a"])));

        IReadOnlyDictionary<string, IReadOnlyList<string>> effective =
            DependencyGraph.BuildEffectiveNeeds(pipeline);

        Assert.Equal(["a"], effective["b"]);
        Assert.Empty(effective["a"]);
    }
}
