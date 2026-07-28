using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public sealed class BuiltinEnvironmentTests
{
    private const string CommitHash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Apply_InjectsContextVariablesIntoEachJob()
    {
        PipelineDefinition pipeline = TestPipelineFactory.Pipeline(
            ("build", TestPipelineFactory.Job()),
            ("test", TestPipelineFactory.Job()));
        pipeline.Name = "my-pipeline";

        BuiltinEnvironment.Apply(pipeline, CommitHash, "main");

        JobDefinition build = pipeline.Jobs["build"];
        Assert.Equal("true", build.Env["CI"]);
        Assert.Equal("true", build.Env["SEBASTIAN_CI"]);
        Assert.Equal("my-pipeline", build.Env["SEBASTIAN_CI_PIPELINE"]);
        Assert.Equal("build", build.Env["SEBASTIAN_CI_JOB"]);
        Assert.Equal(CommitHash, build.Env["SEBASTIAN_CI_COMMIT"]);
        Assert.Equal("01234567", build.Env["SEBASTIAN_CI_COMMIT_SHORT"]);
        Assert.Equal("main", build.Env["SEBASTIAN_CI_BRANCH"]);
        Assert.Equal("test", pipeline.Jobs["test"].Env["SEBASTIAN_CI_JOB"]);
    }

    [Fact]
    public void Apply_DoesNotOverrideUserDefinedValues()
    {
        PipelineDefinition pipeline = TestPipelineFactory.Pipeline(("build", TestPipelineFactory.Job()));
        pipeline.Jobs["build"].Env["CI"] = "custom";

        BuiltinEnvironment.Apply(pipeline, CommitHash, "main");

        Assert.Equal("custom", pipeline.Jobs["build"].Env["CI"]);
    }

    [Fact]
    public void Apply_HandlesShortCommitHash()
    {
        PipelineDefinition pipeline = TestPipelineFactory.Pipeline(("build", TestPipelineFactory.Job()));

        BuiltinEnvironment.Apply(pipeline, "abc", "main");

        Assert.Equal("abc", pipeline.Jobs["build"].Env["SEBASTIAN_CI_COMMIT_SHORT"]);
    }
}
