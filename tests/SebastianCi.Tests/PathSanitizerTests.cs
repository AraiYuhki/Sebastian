using SebastianCi.Core;

namespace SebastianCi.Tests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("restore-job")]
    [InlineData("test_1.0")]
    public void ToFileSystemName_LeavesSafeNamesUnchanged(string safeName)
        => Assert.Equal(safeName, PathSanitizer.ToFileSystemName(safeName));

    [Fact]
    public void ToFileSystemName_ReplacesUnsafeCharacters()
    {
        string result = PathSanitizer.ToFileSystemName("test[image=repo/name:tag]");

        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('[', result);
    }

    [Fact]
    public void ToFileSystemName_ProducesDistinctNamesForDistinctUnsafeInputs()
    {
        string first = PathSanitizer.ToFileSystemName("test[config=Debug]");
        string second = PathSanitizer.ToFileSystemName("test[config=Release]");

        Assert.NotEqual(first, second);
    }
}
