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
}
