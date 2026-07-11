using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// podman コマンドによるコンテナ実行と、その出力のストリーミング回収だけを担当する。
/// </summary>
public sealed class PodmanRunner
{
    private const string PodmanExecutable = "podman";
    private const string ContainerWorkspacePath = "/workspace";
    private const string ShellExecutable = "/bin/sh";

    private readonly string _hostWorkspacePath;
    private readonly string _logDirectoryPath;

    public PodmanRunner(string hostWorkspacePath, string logDirectoryPath)
    {
        _hostWorkspacePath = hostWorkspacePath;
        _logDirectoryPath = logDirectoryPath;
    }

    /// <summary>
    /// ジョブを podman run で実行し、標準出力・標準エラーをリアルタイムに
    /// コンソール表示とログファイル保存の両方へ流す。
    /// </summary>
    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        string logFilePath = Path.Combine(_logDirectoryPath, $"{jobId}.log");
        await using StreamWriter logWriter = new(logFilePath, append: false);

        using Process process = new() { StartInfo = BuildStartInfo(job) };
        if (!process.Start())
        {
            throw new ContainerExecutionException($"podman プロセスの起動に失敗しました (ジョブ: {jobId})");
        }

        await StreamOutputAsync(process, jobId, logWriter, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await logWriter.FlushAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new ContainerExecutionException(
                $"コンテナが終了コード {process.ExitCode} で異常終了しました (ジョブ: {jobId}, ログ: {logFilePath})");
        }
    }

    private ProcessStartInfo BuildStartInfo(JobDefinition job)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = PodmanExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in BuildRunArguments(job))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private IEnumerable<string> BuildRunArguments(JobDefinition job)
    {
        yield return "run";
        yield return "--rm";
        yield return "--volume";
        yield return $"{_hostWorkspacePath}:{ContainerWorkspacePath}";
        yield return "--workdir";
        yield return ContainerWorkspacePath;
        yield return job.Image;
        yield return ShellExecutable;
        yield return "-c";
        yield return string.Join(" && ", job.Commands);
    }

    private static async Task StreamOutputAsync(
        Process process, string jobId, StreamWriter logWriter, CancellationToken cancellationToken)
    {
        using SemaphoreSlim logLock = new(1, 1);
        Task pumpStandardOutput = PumpStreamAsync(
            process.StandardOutput, jobId, isError: false, logWriter, logLock, cancellationToken);
        Task pumpStandardError = PumpStreamAsync(
            process.StandardError, jobId, isError: true, logWriter, logLock, cancellationToken);
        await Task.WhenAll(pumpStandardOutput, pumpStandardError);
    }

    private static async Task PumpStreamAsync(
        StreamReader reader, string jobId, bool isError,
        StreamWriter logWriter, SemaphoreSlim logLock, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            ConsoleLogger.WriteJobOutput(jobId, line, isError);
            await AppendLogLineAsync(logWriter, logLock, line, cancellationToken);
        }
    }

    private static async Task AppendLogLineAsync(
        StreamWriter logWriter, SemaphoreSlim logLock, string line, CancellationToken cancellationToken)
    {
        await logLock.WaitAsync(cancellationToken);
        try
        {
            await logWriter.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        finally
        {
            logLock.Release();
        }
    }
}
