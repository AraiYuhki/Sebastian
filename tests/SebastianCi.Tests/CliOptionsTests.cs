using SebastianCi;

namespace SebastianCi.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_WithNoArguments_UsesDefaults()
    {
        CliOptions? options = CliOptions.Parse([]);

        Assert.NotNull(options);
        Assert.Equal(".", options.RepositoryPath);
        Assert.False(options.IsRebuildRequired);
        Assert.Null(options.EngineName);
        Assert.Empty(options.TargetJobIds);
        Assert.False(options.IsValidateOnly);
    }

    [Fact]
    public void Parse_ReadsValidateFlag()
    {
        CliOptions? options = CliOptions.Parse(["--validate"]);

        Assert.NotNull(options);
        Assert.True(options.IsValidateOnly);
    }

    [Fact]
    public void Parse_ReadsRepositoryPathAndFlags()
    {
        CliOptions? options = CliOptions.Parse(["/repo", "--rebuild", "--config", "custom.yaml"]);

        Assert.NotNull(options);
        Assert.Equal("/repo", options.RepositoryPath);
        Assert.True(options.IsRebuildRequired);
        Assert.Equal("custom.yaml", options.ConfigFileName);
    }

    [Fact]
    public void Parse_CollectsMultipleJobTargets()
    {
        CliOptions? options = CliOptions.Parse(["--job", "test", "--job", "lint"]);

        Assert.NotNull(options);
        Assert.Equal(["test", "lint"], options.TargetJobIds);
    }

    [Theory]
    [InlineData("podman")]
    [InlineData("docker")]
    public void Parse_AcceptsSupportedEngines(string engineName)
    {
        CliOptions? options = CliOptions.Parse(["--engine", engineName]);

        Assert.NotNull(options);
        Assert.Equal(engineName, options.EngineName);
    }

    [Fact]
    public void Parse_RejectsUnsupportedEngine()
        => Assert.Null(CliOptions.Parse(["--engine", "containerd"]));

    [Fact]
    public void Parse_RejectsUnknownFlag()
        => Assert.Null(CliOptions.Parse(["--unknown"]));

    [Fact]
    public void Parse_RejectsFlagMissingItsValue()
        => Assert.Null(CliOptions.Parse(["--config"]));

    [Fact]
    public void Parse_ReadsMaxParallel()
    {
        CliOptions? options = CliOptions.Parse(["--max-parallel", "4"]);

        Assert.NotNull(options);
        Assert.Equal(4, options.MaxParallel);
    }

    [Fact]
    public void Parse_MaxParallelDefaultsToNull()
        => Assert.Null(CliOptions.Parse([])!.MaxParallel);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Parse_RejectsInvalidMaxParallel(string value)
        => Assert.Null(CliOptions.Parse(["--max-parallel", value]));

    [Fact]
    public void Parse_ReadsParameters()
    {
        CliOptions? options = CliOptions.Parse(["--param", "target=production", "--param", "version=1.2.3"]);

        Assert.NotNull(options);
        Assert.Equal("production", options.Parameters["target"]);
        Assert.Equal("1.2.3", options.Parameters["version"]);
    }

    [Fact]
    public void Parse_AllowsEqualsSignInParameterValue()
    {
        CliOptions? options = CliOptions.Parse(["--param", "flags=a=b"]);

        Assert.NotNull(options);
        Assert.Equal("a=b", options.Parameters["flags"]);
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=value")]
    public void Parse_RejectsInvalidParameter(string value)
        => Assert.Null(CliOptions.Parse(["--param", value]));
}
