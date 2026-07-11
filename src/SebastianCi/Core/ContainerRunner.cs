using System.ComponentModel;
using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// コンテナ（Podman / Docker）によるジョブ実行と、その出力のストリーミング回収だけを担当する。
/// ワークスペースは /workspace にバインドマウントし、そこを作業ディレクトリとして実行する。
/// コンテナは --rm 付きで起動するため、終了時に自動で破棄される。
/// </summary>
public sealed class ContainerRunner
{
    private const string ContainerWorkspacePath = "/workspace";
    private const string ShellExecutable = "/bin/sh";

    private readonly ContainerEngine _engine;
    private readonly string _hostWorkspacePath;
    private readonly string _logDirectoryPath;

    public ContainerRunner(ContainerEngine engine, string hostWorkspacePath, string logDirectoryPath)
    {
        _engine = engine;
        _hostWorkspacePath = ResolveHostWorkspacePath(hostWorkspacePath);
        _logDirectoryPath = logDirectoryPath;
    }

    /// <summary>
    /// ジョブをコンテナで実行し、標準出力・標準エラーをリアルタイムに
    /// コンソール表示とログファイル保存の両方へ流す。
    /// </summary>
    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        string logFilePath = Path.Combine(_logDirectoryPath, $"{jobId}.log");
        await using StreamWriter logWriter = new(logFilePath, append: false);

        using Process process = new() { StartInfo = BuildStartInfo(job) };
        StartProcess(process, jobId);

        await StreamOutputAsync(process, jobId, logWriter, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await logWriter.FlushAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new ContainerExecutionException(
                $"コンテナが終了コード {process.ExitCode} で異常終了しました (ジョブ: {jobId}, ログ: {logFilePath})");
        }
    }

    private void StartProcess(Process process, string jobId)
    {
        try
        {
            if (!process.Start())
            {
                throw new ContainerExecutionException(
                    $"{_engine.ExecutableName} プロセスの起動に失敗しました (ジョブ: {jobId})");
            }
        }
        catch (Win32Exception exception)
        {
            throw new ContainerExecutionException(
                $"{_engine.ExecutableName} を起動できません (ジョブ: {jobId}): {exception.Message}");
        }
    }

    private ProcessStartInfo BuildStartInfo(JobDefinition job)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _engine.ExecutableName,
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

        foreach ((string key, string value) in job.Env)
        {
            yield return "-e";
            yield return $"{key}={value}";
        }

        yield return "--volume";
        yield return _engine.BuildWorkspaceMountArgument(_hostWorkspacePath, ContainerWorkspacePath);
        yield return "--workdir";
        yield return ContainerWorkspacePath;
        yield return job.Image;
        yield return ShellExecutable;
        yield return "-c";
        yield return string.Join(" && ", job.Script);
    }

    private static string ResolveHostWorkspacePath(string hostWorkspacePath)
    {
        string fullPath = Path.GetFullPath(hostWorkspacePath);
        if (!Directory.Exists(fullPath))
        {
            throw new ContainerExecutionException($"マウント対象のワークスペースが存在しません: {fullPath}");
        }

        return fullPath;
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
