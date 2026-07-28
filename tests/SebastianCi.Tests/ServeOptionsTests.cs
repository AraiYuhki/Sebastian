using SebastianCi.Web;

namespace SebastianCi.Tests;

public class ServeOptionsTests
{
    [Fact]
    public void Parse_UsesDefaults()
    {
        ServeOptions? options = ServeOptions.Parse([]);

        Assert.NotNull(options);
        Assert.Equal(".", options.RepositoryPath);
        Assert.Equal(ServeOptions.DefaultPort, options.Port);
    }

    [Fact]
    public void Parse_ReadsPathAndPort()
    {
        ServeOptions? options = ServeOptions.Parse(["/repo", "--port", "9000"]);

        Assert.NotNull(options);
        Assert.Equal("/repo", options.RepositoryPath);
        Assert.Equal(9000, options.Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void Parse_RejectsInvalidPort(string port)
        => Assert.Null(ServeOptions.Parse(["--port", port]));

    [Fact]
    public void Parse_ReadsToken()
        => Assert.Equal("my-secret", ServeOptions.Parse(["--token", "my-secret"])!.Token);

    [Fact]
    public void Parse_TokenDefaultsToNull()
        => Assert.Null(ServeOptions.Parse([])!.Token);

    [Fact]
    public void Parse_ReadsTokenFromEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(ServeOptions.TokenEnvironmentVariableName, "env-token");
        try
        {
            Assert.Equal("env-token", ServeOptions.Parse([])!.Token);
            Assert.Equal("cli-token", ServeOptions.Parse(["--token", "cli-token"])!.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServeOptions.TokenEnvironmentVariableName, null);
        }
    }
}
