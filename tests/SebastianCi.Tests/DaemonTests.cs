using SebastianCi.Web;

namespace SebastianCi.Tests;

public class DaemonTests
{
    [Theory]
    [InlineData("start", DaemonAction.Start)]
    [InlineData("stop", DaemonAction.Stop)]
    [InlineData("restart", DaemonAction.Restart)]
    [InlineData("status", DaemonAction.Status)]
    [InlineData("run", DaemonAction.Run)]
    public void Parse_ReadsAction(string verb, DaemonAction expected)
        => Assert.Equal(expected, DaemonOptions.Parse([verb])!.Action);

    [Fact]
    public void Parse_PassesServeArguments()
    {
        DaemonOptions? options = DaemonOptions.Parse(["start", "/repo", "--port", "9000", "--token", "t"]);

        Assert.NotNull(options);
        Assert.Equal("/repo", options.Serve.RepositoryPath);
        Assert.Equal(9000, options.Serve.Port);
        Assert.Equal("t", options.Serve.Token);
    }

    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("start", "--port", "abc")]
    public void Parse_RejectsInvalid(params string[] args)
        => Assert.Null(DaemonOptions.Parse(args));

    [Fact]
    public void ToServeArguments_OmitsToken()
    {
        ServeOptions options = ServeOptions.Parse(["/repo", "--port", "9000", "--token", "secret"])!;

        Assert.DoesNotContain("secret", options.ToServeArguments());
        Assert.Contains("9000", options.ToServeArguments());
    }

    [Fact]
    public void State_RoundTripsAndDetectsDeadSupervisor()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sebastian-daemon-" + Guid.NewGuid().ToString("N"));
        try
        {
            DaemonState state = new(
                int.MaxValue, DateTimeOffset.UtcNow, 8080, "/repo", null, 2, 1, null);
            state.Save(directory);

            DaemonState? loaded = DaemonState.Load(directory);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.RestartCount);
            Assert.False(loaded.IsSupervisorAlive());
            DaemonState.Delete(directory);
            Assert.Null(DaemonState.Load(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void State_IgnoresPidReusedByDifferentProcess()
    {
        DaemonState state = new(
            Environment.ProcessId, DateTimeOffset.UtcNow.AddDays(-30), 8080, "/repo", null, 0, null, null);

        Assert.False(state.IsSupervisorAlive());
    }

    [Fact]
    public void StdHandleGuard_CanBeAppliedAndRestoredRepeatedly()
    {
        // 標準ハンドルの継承を一時的に止めて元に戻す処理が、何度繰り返しても例外を出さないこと。
        for (int i = 0; i < 3; i++)
        {
            using (StdHandleGuard.SuppressInheritance()) { }
        }
    }
}
