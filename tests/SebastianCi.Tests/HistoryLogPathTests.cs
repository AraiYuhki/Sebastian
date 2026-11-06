using SebastianCi.Core;

namespace SebastianCi.Tests;

public class HistoryLogPathTests
{
    [Theory]
    [InlineData("0fdf170", true)]
    [InlineData("0fdf170fdc7bf574d512c80991d2b90159453aff", true)]
    [InlineData("ABCDEF1234", true)]
    [InlineData("abc", false)]
    [InlineData("..", false)]
    [InlineData("../..", false)]
    [InlineData("0fdf170/../../x", false)]
    [InlineData("0fdf170\\..\\x", false)]
    [InlineData("zzzzzzzz", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidCommitKey_AcceptsOnlyHexHashes(string? key, bool expected)
        => Assert.Equal(expected, HistoryManager.IsValidCommitKey(key));

    [Fact]
    public async Task ReadJobLogAsync_ReadsOwnLogsButNeverEscapesTheLogDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "sebastian-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string logDir = Path.Combine(root, "data", "logs", "0fdf170");
            Directory.CreateDirectory(logDir);
            await File.WriteAllTextAsync(Path.Combine(logDir, "build.log"), "inside");
            await File.WriteAllTextAsync(Path.Combine(root, "secret.log"), "outside");
            HistoryManager history = new(Path.Combine(root, "data"));

            Assert.Equal("inside", await history.ReadJobLogAsync("0fdf170", "build"));
            Assert.Null(await history.ReadJobLogAsync("../..", "secret"));
            Assert.Null(await history.ReadJobLogAsync("../../..", "secret"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
