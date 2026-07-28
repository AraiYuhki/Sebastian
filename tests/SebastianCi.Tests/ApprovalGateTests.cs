using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public sealed class ApprovalGateTests
{
    [Fact]
    public async Task RequestAsync_AutoApproveReturnsTrueWithoutReadingInput()
    {
        ConsoleApprovalGate gate = new(autoApprove: true, input: new StringReader(""));

        Assert.True(await gate.RequestAsync("deploy", "本番へデプロイします"));
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("  y  ")]
    public async Task RequestAsync_ApprovesOnYesAnswer(string answer)
    {
        ConsoleApprovalGate gate = new(autoApprove: false, input: new StringReader(answer + "\n"));

        Assert.True(await gate.RequestAsync("deploy", "確認"));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData("なにか別の入力")]
    public async Task RequestAsync_DeniesOnOtherAnswer(string answer)
    {
        ConsoleApprovalGate gate = new(autoApprove: false, input: new StringReader(answer + "\n"));

        Assert.False(await gate.RequestAsync("deploy", "確認"));
    }

    [Fact]
    public async Task RequestAsync_DeniesWhenInputIsClosed()
    {
        ConsoleApprovalGate gate = new(autoApprove: false, input: new StringReader(""));

        Assert.False(await gate.RequestAsync("deploy", "確認"));
    }
}

public sealed class DagEngineApprovalTests : IDisposable
{
    private readonly string _tempDirectory;

    public DagEngineApprovalTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"sebastian-ci-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public async Task ExecuteAsync_DeniedApprovalFailsJobAndSkipsDependents()
    {
        RecordingRunner runner = new();
        DagEngine engine = CreateEngine(runner, new StubGate(approve: false));
        PipelineDefinition pipeline = TestPipelineFactory.Pipeline(
            ("deploy", JobWithApproval("本番へデプロイしてよいですか？")),
            ("announce", TestPipelineFactory.Job(needs: ["deploy"])));

        IReadOnlyList<JobResult> results = await engine.ExecuteAsync(pipeline);

        Assert.Equal(JobStatus.Failed, results.Single(r => r.JobId == "deploy").Status);
        Assert.Equal(JobStatus.Skipped, results.Single(r => r.JobId == "announce").Status);
        Assert.Empty(runner.ExecutedJobIds);
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedJobRuns()
    {
        RecordingRunner runner = new();
        DagEngine engine = CreateEngine(runner, new StubGate(approve: true));
        PipelineDefinition pipeline = TestPipelineFactory.Pipeline(("deploy", JobWithApproval("確認")));

        IReadOnlyList<JobResult> results = await engine.ExecuteAsync(pipeline);

        Assert.Equal(JobStatus.Success, results.Single().Status);
        Assert.Equal(["deploy"], runner.ExecutedJobIds);
    }

    [Fact]
    public async Task ExecuteAsync_JobsWithoutApprovalDoNotConsultGate()
    {
        RecordingRunner runner = new();
        StubGate gate = new(approve: false);
        DagEngine engine = CreateEngine(runner, gate);
        PipelineDefinition pipeline = TestPipelineFactory.Pipeline(("build", TestPipelineFactory.Job()));

        IReadOnlyList<JobResult> results = await engine.ExecuteAsync(pipeline);

        Assert.Equal(JobStatus.Success, results.Single().Status);
        Assert.Equal(0, gate.RequestCount);
    }

    private DagEngine CreateEngine(IJobRunner runner, IApprovalGate gate)
    {
        JobRunnerSelector selector = new(runner, runner, runner, runner, new Dictionary<string, IJobRunner>());
        ArtifactManager artifactManager = new(_tempDirectory, _tempDirectory, "commit");
        return new DagEngine(selector, artifactManager, new ChangeDetector(null), approvalGate: gate);
    }

    private static JobDefinition JobWithApproval(string message)
    {
        JobDefinition job = TestPipelineFactory.Job();
        job.Approval = message;
        return job;
    }

    private sealed class StubGate : IApprovalGate
    {
        private readonly bool _approve;

        public StubGate(bool approve) => _approve = approve;

        public int RequestCount { get; private set; }

        public Task<bool> RequestAsync(string jobId, string message, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(_approve);
        }
    }

    private sealed class RecordingRunner : IJobRunner
    {
        private readonly List<string> _executedJobIds = new();

        public IReadOnlyList<string> ExecutedJobIds => _executedJobIds;

        public Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
        {
            lock (_executedJobIds)
            {
                _executedJobIds.Add(jobId);
            }

            return Task.CompletedTask;
        }
    }
}
