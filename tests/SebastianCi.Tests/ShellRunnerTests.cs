using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public sealed class ShellRunnerTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly string _logDirectoryPath;

    public ShellRunnerTests()
    {
        string root = Path.Combine(Path.GetTempPath(), $"sebastian-ci-shell-test-{Guid.NewGuid():N}");
        _workspacePath = Path.Combine(root, "workspace");
        _logDirectoryPath = Path.Combine(root, "logs");
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(_logDirectoryPath);
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_workspacePath)!, recursive: true);

    [Fact]
    public async Task RunJobAsync_ExecutesScriptOnHostAndWritesLog()
    {
        List<string> observed = new();
        ShellRunner runner = new(_workspacePath, _logDirectoryPath, (line, _) => observed.Add(line));
        JobDefinition job = new() { Shell = true, Script = ["echo first", "echo second"] };

        await runner.RunJobAsync("hello", job);

        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectoryPath, "hello.log"));
        Assert.Contains("first", log);
        Assert.Contains("second", log);
        Assert.Contains("first", observed);
    }

    [Fact]
    public async Task RunJobAsync_RunsInWorkspaceDirectory()
    {
        ShellRunner runner = new(_workspacePath, _logDirectoryPath);
        JobDefinition job = new() { Shell = true, Script = [OperatingSystem.IsWindows() ? "cd" : "pwd"] };

        await runner.RunJobAsync("where", job);

        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectoryPath, "where.log"));
        Assert.Contains(new DirectoryInfo(_workspacePath).Name, log);
    }

    [Fact]
    public async Task RunJobAsync_PassesJobEnvironmentVariables()
    {
        ShellRunner runner = new(_workspacePath, _logDirectoryPath);
        JobDefinition job = new()
        {
            Shell = true,
            Env = new() { ["GREETING"] = "hello-from-env" },
            Script = [OperatingSystem.IsWindows() ? "echo %GREETING%" : "echo $GREETING"]
        };

        await runner.RunJobAsync("env", job);

        string log = await File.ReadAllTextAsync(Path.Combine(_logDirectoryPath, "env.log"));
        Assert.Contains("hello-from-env", log);
    }

    [Fact]
    public async Task RunJobAsync_ThrowsOnNonZeroExitCode()
    {
        ShellRunner runner = new(_workspacePath, _logDirectoryPath);
        JobDefinition job = new() { Shell = true, Script = ["exit 3"] };

        ContainerExecutionException exception =
            await Assert.ThrowsAsync<ContainerExecutionException>(() => runner.RunJobAsync("fail", job));
        Assert.Contains("3", exception.Message);
    }

    [Fact]
    public async Task RunJobAsync_ThrowsOnTimeout()
    {
        ShellRunner runner = new(_workspacePath, _logDirectoryPath);
        JobDefinition job = new()
        {
            Shell = true,
            Timeout = 1,
            Script = [OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30"]
        };

        ContainerExecutionException exception =
            await Assert.ThrowsAsync<ContainerExecutionException>(() => runner.RunJobAsync("slow", job));
        Assert.Contains("制限時間", exception.Message);
    }

    [Fact]
    public void Constructor_ThrowsWhenWorkspaceMissing()
        => Assert.Throws<ContainerExecutionException>(
            () => new ShellRunner(Path.Combine(_workspacePath, "missing"), _logDirectoryPath));
}
