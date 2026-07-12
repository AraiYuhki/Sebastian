using SebastianCi.Core;
using SebastianCi.Models;
using static SebastianCi.Tests.TestPipelineFactory;

namespace SebastianCi.Tests;

public class MatrixExpanderTests
{
    [Fact]
    public void Expand_GeneratesAllCombinations()
    {
        JobDefinition test = Job();
        test.Matrix = new()
        {
            ["image"] = ["sdk:8.0", "sdk:9.0"],
            ["configuration"] = ["Debug", "Release"]
        };
        PipelineDefinition pipeline = Pipeline(("test", test));

        MatrixExpander.Expand(pipeline);

        Assert.Equal(4, pipeline.Jobs.Count);
    }

    [Fact]
    public void Expand_SwitchesImageAndInjectsOtherKeysAsEnv()
    {
        JobDefinition test = Job(image: "fallback");
        test.Matrix = new()
        {
            ["image"] = ["sdk:9.0"],
            ["configuration"] = ["Release"]
        };
        PipelineDefinition pipeline = Pipeline(("test", test));

        MatrixExpander.Expand(pipeline);
        JobDefinition variant = pipeline.Jobs.Values.Single();

        Assert.Equal("sdk:9.0", variant.Image);
        Assert.Equal("Release", variant.Env["configuration"]);
        Assert.DoesNotContain("image", variant.Env.Keys);
    }

    [Fact]
    public void Expand_RewritesDependentNeedsToAllVariants()
    {
        JobDefinition test = Job();
        test.Matrix = new() { ["configuration"] = ["Debug", "Release"] };
        JobDefinition deploy = Job(needs: ["test"]);
        PipelineDefinition pipeline = Pipeline(("test", test), ("deploy", deploy));

        MatrixExpander.Expand(pipeline);

        JobDefinition expandedDeploy = pipeline.Jobs["deploy"];
        Assert.Equal(2, expandedDeploy.Needs.Count);
        Assert.All(expandedDeploy.Needs, needId => Assert.StartsWith("test[", needId));
    }

    [Fact]
    public void Expand_LeavesNonMatrixJobsUnchanged()
    {
        PipelineDefinition pipeline = Pipeline(("plain", Job()));

        MatrixExpander.Expand(pipeline);

        Assert.True(pipeline.Jobs.ContainsKey("plain"));
    }

    [Fact]
    public void Expand_PropagatesScalarSettingsToVariants()
    {
        JobDefinition test = Job();
        test.Matrix = new() { ["configuration"] = ["Debug", "Release"] };
        test.Timeout = 120;
        test.Retry = 2;
        test.ContinueOnError = true;
        test.Cache = ["/root/.nuget/packages"];
        PipelineDefinition pipeline = Pipeline(("test", test));

        MatrixExpander.Expand(pipeline);

        Assert.All(pipeline.Jobs.Values, variant =>
        {
            Assert.Equal(120, variant.Timeout);
            Assert.Equal(2, variant.Retry);
            Assert.True(variant.ContinueOnError);
            Assert.Equal(["/root/.nuget/packages"], variant.Cache);
        });
    }
}
