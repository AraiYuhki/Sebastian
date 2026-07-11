using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public sealed class PipelineParserTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PipelineParser _parser = new();

    public PipelineParserTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"sebastian-ci-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public async Task ParseAsync_ReadsValidPipeline()
    {
        string path = WriteConfig("""
            image: alpine
            env:
              GLOBAL: value
            jobs:
              build:
                script:
                  - echo build
            """);

        PipelineDefinition pipeline = await _parser.ParseAsync(path);

        Assert.Single(pipeline.Jobs);
        Assert.Equal("alpine", pipeline.Jobs["build"].Image);
    }

    [Fact]
    public async Task ParseAsync_InheritsGlobalImageAndMergesEnv()
    {
        string path = WriteConfig("""
            image: global-image
            env:
              SHARED: global
              ONLY_GLOBAL: g
            jobs:
              build:
                env:
                  SHARED: job
                script: [echo hi]
            """);

        JobDefinition job = (await _parser.ParseAsync(path)).Jobs["build"];

        Assert.Equal("global-image", job.Image);
        Assert.Equal("job", job.Env["SHARED"]);
        Assert.Equal("g", job.Env["ONLY_GLOBAL"]);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForMissingFile()
    {
        string missingPath = Path.Combine(_tempDirectory, "does-not-exist.yaml");

        await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(missingPath));
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenImageMissing()
    {
        string path = WriteConfig("""
            jobs:
              build:
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("image", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenScriptMissing()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                needs: []
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("script", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForUnknownNeeds()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                needs: [ghost]
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("ghost", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForCyclicNeeds()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              a:
                needs: [b]
                script: [echo a]
              b:
                needs: [a]
                script: [echo b]
            """);

        await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
    }

    [Fact]
    public async Task ParseAsync_ThrowsForUnknownKey()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                commands: [echo old]
                script: [echo hi]
            """);

        await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
    }

    [Fact]
    public async Task ParseAsync_ExpandsMatrixIntoConcreteJobs()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              test:
                matrix:
                  configuration: [Debug, Release]
                script: [echo test]
            """);

        PipelineDefinition pipeline = await _parser.ParseAsync(path);

        Assert.Equal(2, pipeline.Jobs.Count);
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenMatrixExceedsCombinationLimit()
    {
        List<string> manyValues = Enumerable.Range(0, 8).Select(index => $"v{index}").ToList();
        string values = string.Join(", ", manyValues);
        string path = WriteConfig($"""
            image: alpine
            jobs:
              test:
                matrix:
                  a: [{values}]
                  b: [{values}]
                script: [echo test]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("上限", exception.Message);
    }

    private string WriteConfig(string yaml)
    {
        string path = Path.Combine(_tempDirectory, ".sebastian-ci.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
