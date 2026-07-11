using SebastianCi.Core;
using SebastianCi.Models;
using static SebastianCi.Tests.TestPipelineFactory;

namespace SebastianCi.Tests;

public class JobSelectorTests
{
    [Fact]
    public void SelectTargets_KeepsTargetAndTransitiveDependencies()
    {
        PipelineDefinition pipeline = Pipeline(
            ("restore", Job()),
            ("build", Job(needs: ["restore"])),
            ("test", Job(needs: ["build"])),
            ("lint", Job()));

        JobSelector.SelectTargets(pipeline, ["test"]);

        Assert.Equal(["build", "restore", "test"], pipeline.Jobs.Keys.OrderBy(id => id).ToArray());
        Assert.False(pipeline.Jobs.ContainsKey("lint"));
    }

    [Fact]
    public void SelectTargets_WithEmptyTargets_KeepsEverything()
    {
        PipelineDefinition pipeline = Pipeline(("a", Job()), ("b", Job()));

        JobSelector.SelectTargets(pipeline, []);

        Assert.Equal(2, pipeline.Jobs.Count);
    }

    [Fact]
    public void SelectTargets_ByOriginalId_SelectsAllMatrixVariants()
    {
        JobDefinition test = Job();
        test.Matrix = new() { ["configuration"] = ["Debug", "Release"] };
        PipelineDefinition pipeline = Pipeline(("test", test), ("lint", Job()));
        MatrixExpander.Expand(pipeline);

        JobSelector.SelectTargets(pipeline, ["test"]);

        Assert.Equal(2, pipeline.Jobs.Count);
        Assert.All(pipeline.Jobs.Keys, id => Assert.StartsWith("test[", id));
    }

    [Fact]
    public void SelectTargets_ThrowsForUnknownJob()
    {
        PipelineDefinition pipeline = Pipeline(("a", Job()));

        InvalidPipelineException exception =
            Assert.Throws<InvalidPipelineException>(() => JobSelector.SelectTargets(pipeline, ["ghost"]));
        Assert.Contains("ghost", exception.Message);
    }
}
