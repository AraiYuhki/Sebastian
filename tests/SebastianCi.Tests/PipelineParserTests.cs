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

    [Fact]
    public async Task ParseAsync_ThrowsForNegativeTimeout()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                timeout: -5
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("timeout", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ReadsTimeout()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                timeout: 30
                script: [echo hi]
            """);

        PipelineDefinition pipeline = await _parser.ParseAsync(path);

        Assert.Equal(30, pipeline.Jobs["build"].Timeout);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForNegativeRetry()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                retry: -1
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("retry", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ReadsRetryAndContinueOnError()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                retry: 3
                continueOnError: true
                script: [echo hi]
            """);

        JobDefinition job = (await _parser.ParseAsync(path)).Jobs["build"];

        Assert.Equal(3, job.Retry);
        Assert.True(job.ContinueOnError);
    }

    [Fact]
    public async Task ParseAsync_ReadsCachePaths()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                cache:
                  - /root/.nuget/packages
                script: [dotnet build]
            """);

        JobDefinition job = (await _parser.ParseAsync(path)).Jobs["build"];

        Assert.Equal(["/root/.nuget/packages"], job.Cache);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForRelativeCachePath()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                cache: [relative/dir]
                script: [dotnet build]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("cache", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ReadsSlackNotification()
    {
        string path = WriteConfig("""
            image: alpine
            notifications:
              - type: slack
                on: [failure]
                webhook: https://hooks.slack.com/services/XXX
            jobs:
              build:
                script: [echo hi]
            """);

        NotificationConfig notification = (await _parser.ParseAsync(path)).Notifications.Single();

        Assert.Equal("slack", notification.Type);
        Assert.Equal(["failure"], notification.On);
        Assert.Equal("https://hooks.slack.com/services/XXX", notification.Webhook);
    }

    [Fact]
    public async Task ParseAsync_AcceptsUnknownNotificationTypeForPlugins()
    {
        // 未知の type はプラグイン提供の可能性があるため、パース段階では受け入れる
        // （対応チャンネルが無い場合は実行時に解決エラーとなる）。
        string path = WriteConfig("""
            image: alpine
            notifications:
              - type: teams
                on: [failure]
            jobs:
              build:
                script: [echo hi]
            """);

        PipelineDefinition pipeline = await _parser.ParseAsync(path);

        Assert.Equal("teams", pipeline.Notifications.Single().Type);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForInvalidNotificationEvent()
    {
        string path = WriteConfig("""
            image: alpine
            notifications:
              - type: slack
                on: [whenever]
                webhook: https://x
            jobs:
              build:
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("whenever", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ThrowsForSlackWithoutWebhook()
    {
        string path = WriteConfig("""
            image: alpine
            notifications:
              - type: slack
                on: [success]
            jobs:
              build:
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("webhook", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_ReadsResourceThresholds()
    {
        string path = WriteConfig("""
            image: alpine
            resources:
              minMemoryMb: 256
              minDiskMb: 2048
            jobs:
              build:
                script: [echo hi]
            """);

        ResourceThresholds resources = (await _parser.ParseAsync(path)).Resources;

        Assert.Equal(256, resources.MinMemoryMb);
        Assert.Equal(2048, resources.MinDiskMb);
    }

    [Fact]
    public async Task ParseAsync_ReadsPluginReferences()
    {
        string path = WriteConfig("""
            image: alpine
            plugins:
              - package: SebastianCi.Extra
                version: 1.2.3
              - path: ./plugins/My.dll
            jobs:
              build:
                script: [echo hi]
            """);

        List<PluginReference> plugins = (await _parser.ParseAsync(path)).Plugins;

        Assert.Equal(2, plugins.Count);
        Assert.True(plugins[0].IsPackage);
        Assert.Equal("1.2.3", plugins[0].Version);
        Assert.Equal("./plugins/My.dll", plugins[1].Path);
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenPluginHasBothPackageAndPath()
    {
        string path = WriteConfig("""
            image: alpine
            plugins:
              - package: X
                path: ./y.dll
            jobs:
              build:
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("plugins", exception.Message);
    }

    [Fact]
    public async Task ParseAsync_AllowsPluginProvidedNotificationType()
    {
        string path = WriteConfig("""
            image: alpine
            plugins:
              - path: ./plugins/My.dll
            notifications:
              - type: webhook
                on: [success]
                webhook: https://example.com/hook
            jobs:
              build:
                script: [echo hi]
            """);

        // 未知の type でもパース段階では拒否しない（実行時にチャンネル解決）
        PipelineDefinition pipeline = await _parser.ParseAsync(path);

        Assert.Equal("webhook", pipeline.Notifications.Single().Type);
    }

    [Fact]
    public async Task ParseAsync_ReadsRunner()
    {
        string path = WriteConfig("""
            image: alpine
            jobs:
              build:
                runner: kubernetes
                script: [echo hi]
            """);

        Assert.Equal("kubernetes", (await _parser.ParseAsync(path)).Jobs["build"].Runner);
    }

    [Fact]
    public async Task ParseAsync_ThrowsWhenRunnerCombinedWithRemote()
    {
        string path = WriteConfig("""
            image: alpine
            agents:
              - url: http://a
            jobs:
              build:
                runner: kubernetes
                remote: true
                script: [echo hi]
            """);

        InvalidPipelineException exception =
            await Assert.ThrowsAsync<InvalidPipelineException>(() => _parser.ParseAsync(path));
        Assert.Contains("runner", exception.Message);
    }

    private string WriteConfig(string yaml)
    {
        string path = Path.Combine(_tempDirectory, ".sebastian-ci.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
