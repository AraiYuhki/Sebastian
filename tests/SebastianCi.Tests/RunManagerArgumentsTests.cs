using SebastianCi.Web;

namespace SebastianCi.Tests;

public class RunManagerArgumentsTests
{
    [Fact]
    public void BuildArguments_PassesTheSelectedConfigAndDataDirectory()
    {
        RunManager manager = new("/repo", "sebastian-ci.dashboard.yaml", "/repo/.sebastian-ci");

        string[] arguments = manager.BuildArguments("sebastian-ci.dll", true, "build", null, false).ToArray();

        Assert.Equal(
            ["sebastian-ci.dll", "/repo", "--config", "sebastian-ci.dashboard.yaml", "--data-dir", "/repo/.sebastian-ci",
             "--rebuild", "--job", "build"],
            arguments);
    }

    [Fact]
    public void BuildArguments_OmitsConfigAndDataDirectoryWhenNotGiven()
    {
        RunManager manager = new("/repo");

        string[] arguments = manager.BuildArguments("sebastian-ci.dll", false, null, null, false).ToArray();

        Assert.Equal(["sebastian-ci.dll", "/repo"], arguments);
    }
}
