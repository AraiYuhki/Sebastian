using SebastianCi.Core;
using SebastianCi.Models;
using static SebastianCi.Tests.TestPipelineFactory;

namespace SebastianCi.Tests;

public class JobRunnerSelectorTests
{
    private readonly StubRunner _local = new("local");
    private readonly StubRunner _direct = new("direct");
    private readonly StubRunner _pooled = new("pooled");

    [Fact]
    public void Select_ChoosesLocalByDefault()
        => Assert.Same(_local, Build().Select(Job()));

    [Fact]
    public void Select_ChoosesDirectWhenAgentSet()
    {
        JobDefinition job = Job();
        job.Agent = "http://a";

        Assert.Same(_direct, Build().Select(job));
    }

    [Fact]
    public void Select_ChoosesPooledWhenRemote()
    {
        JobDefinition job = Job();
        job.Remote = true;

        Assert.Same(_pooled, Build().Select(job));
    }

    [Fact]
    public void Select_ChoosesPluginRunnerByName()
    {
        StubRunner plugin = new("plugin");
        JobDefinition job = Job();
        job.Runner = "kube";

        Assert.Same(plugin, Build(new Dictionary<string, IJobRunner> { ["kube"] = plugin }).Select(job));
    }

    [Fact]
    public void Select_ThrowsForUnknownRunner()
    {
        JobDefinition job = Job();
        job.Runner = "ghost";

        Assert.Throws<InvalidPipelineException>(() => Build().Select(job));
    }

    private JobRunnerSelector Build(IReadOnlyDictionary<string, IJobRunner>? plugins = null)
        => new(_local, _direct, _pooled, plugins ?? new Dictionary<string, IJobRunner>());

    private sealed class StubRunner(string id) : IJobRunner
    {
        public string Id { get; } = id;
        public Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
