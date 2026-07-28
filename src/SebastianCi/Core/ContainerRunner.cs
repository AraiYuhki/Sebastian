using System.ComponentModel;
using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// コンテナ（Podman / Docker）によるジョブ実行と、その出力のストリーミング回収だけを担当する。
/// ワークスペースは /workspace にバインドマウントし、そこを作業ディレクトリとして実行する。
/// コンテナは --rm 付きで起動するため、終了時に自動で破棄される。
/// </summary>
public sealed class ContainerRunner : IJobRunner
{
    private const string ContainerWorkspacePath = "/workspace";
    private const string ShellExecutable = "/bin/sh";
    private const string ContainerNamePrefix = "sebastian-ci";

    private readonly ContainerEngine _engine;
    private readonly ActiveContainerRegistry _registry;
    private readonly string _hostWorkspacePath;
    private readonly string _logDirectoryPath;
    private readonly string _cacheRootPath;
    private readonly Action<string, bool>? _outputObserver;

    public ContainerRunner(
        ContainerEngine engine, ActiveContainerRegistry registry,
        string hostWorkspacePath, string logDirectoryPath, string cacheRootPath,
        Action<string, bool>? outputObserver = null)
    {
        _engine = engine;
        _registry = registry;
        _hostWorkspacePath = ResolveHostWorkspacePath(hostWorkspacePath);
        _logDirectoryPath = logDirectoryPath;
        _cacheRootPath = cacheRootPath;
        _outputObserver = outputObserver;
    }

    /// <summary>
    /// ジョブを名前付きコンテナで実行する。実行中はレジストリで追跡し、
    /// 完走後（成否問わず）に追跡を解除する。中断時は解除せず、クリーンアップ側の停止対象として残す。
    /// </summary>
    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        ActiveContainer container = new(_engine, CreateContainerName(jobId));
        _registry.Register(container);

        try
        {
            await RunJobCoreAsync(jobId, job, container.Name, cancellationToken);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) _registry.Unregister(container);
        }
    }

    private async Task RunJobCoreAsync(
        string jobId, JobDefinition job, string containerName, CancellationToken cancellationToken)
    {
        string logFilePath = Path.Combine(_logDirectoryPath, $"{PathSanitizer.ToFileSystemName(jobId)}.log");
        await using StreamWriter logWriter = new(logFilePath, append: false);

        using Process process = new() { StartInfo = BuildStartInfo(job, containerName) };
        StartProcess(process, jobId);
        await AwaitCompletionAsync(process, job, jobId, containerName, logWriter, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new ContainerExecutionException(
                $"コンテナが終了コード {process.ExitCode} で異常終了しました (ジョブ: {jobId}, ログ: {logFilePath})");
        }
    }

    /// <summary>
    /// 出力の回収とプロセス終了を待つ。timeout 指定があり、かつ中断（Ctrl+C）でなく
    /// 制限時間を超過した場合は、コンテナを停止してタイムアウト例外をスローする。
    /// </summary>
    private async Task AwaitCompletionAsync(
        Process process, JobDefinition job, string jobId, string containerName,
        StreamWriter logWriter, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CreateTimeoutSource(job, cancellationToken);
        try
        {
            await StreamOutputAsync(process, jobId, logWriter, timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            await logWriter.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateContainerAsync(containerName);
            throw new ContainerExecutionException(
                $"ジョブ '{jobId}' が制限時間 {job.Timeout} 秒を超過したため中断しました。");
        }
    }

    private static CancellationTokenSource CreateTimeoutSource(JobDefinition job, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (job.Timeout > 0) source.CancelAfter(TimeSpan.FromSeconds(job.Timeout));
        return source;
    }

    private async Task TerminateContainerAsync(string containerName)
    {
        await _engine.ExecuteQuietlyAsync(["stop", "-t", "2", containerName]);
        await _engine.ExecuteQuietlyAsync(["rm", containerName]);
    }

    private static string CreateContainerName(string jobId)
    {
        string sanitizedJobId = new(jobId.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '-').ToArray());
        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        return $"{ContainerNamePrefix}-{sanitizedJobId}-{uniqueSuffix}";
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

    private ProcessStartInfo BuildStartInfo(JobDefinition job, string containerName)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _engine.ExecutableName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in BuildRunArguments(job, containerName))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private IEnumerable<string> BuildRunArguments(JobDefinition job, string containerName)
    {
        yield return "run";
        yield return "--rm";
        yield return "--name";
        yield return containerName;

        foreach ((string key, string value) in job.Env)
        {
            yield return "-e";
            yield return $"{key}={value}";
        }

        foreach (string cacheArgument in BuildCacheArguments(job))
        {
            yield return cacheArgument;
        }

        yield return "--volume";
        yield return _engine.BuildWorkspaceMountArgument(_hostWorkspacePath, ContainerWorkspacePath);
        yield return "--workdir";
        yield return ContainerWorkspacePath;
        yield return job.Image;
        yield return ShellExecutable;
        yield return "-c";
        yield return ScriptComposer.ComposePosix(job);
    }

    /// <summary>
    /// cache に列挙されたコンテナ内パスを、コミット横断で永続化するホスト側ディレクトリへ
    /// バインドマウントする引数を生成する。ホスト側ディレクトリは必要に応じて作成する。
    /// </summary>
    private IEnumerable<string> BuildCacheArguments(JobDefinition job)
    {
        foreach (string containerPath in job.Cache)
        {
            string hostPath = ResolveCacheHostPath(containerPath);
            Directory.CreateDirectory(hostPath);
            yield return "--volume";
            yield return _engine.BuildWorkspaceMountArgument(hostPath, containerPath);
        }
    }

    private string ResolveCacheHostPath(string containerPath)
        => Path.Combine(_cacheRootPath, PathSanitizer.ToFileSystemName(containerPath.TrimStart('/')));

    private static string ResolveHostWorkspacePath(string hostWorkspacePath)
    {
        string fullPath = Path.GetFullPath(hostWorkspacePath);
        if (!Directory.Exists(fullPath))
        {
            throw new ContainerExecutionException($"マウント対象のワークスペースが存在しません: {fullPath}");
        }

        return fullPath;
    }

    private async Task StreamOutputAsync(
        Process process, string jobId, StreamWriter logWriter, CancellationToken cancellationToken)
    {
        using SemaphoreSlim logLock = new(1, 1);
        Task pumpStandardOutput = PumpStreamAsync(
            process.StandardOutput, jobId, isError: false, logWriter, logLock, cancellationToken);
        Task pumpStandardError = PumpStreamAsync(
            process.StandardError, jobId, isError: true, logWriter, logLock, cancellationToken);
        await Task.WhenAll(pumpStandardOutput, pumpStandardError);
    }

    private async Task PumpStreamAsync(
        StreamReader reader, string jobId, bool isError,
        StreamWriter logWriter, SemaphoreSlim logLock, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            string maskedLine = SecretMasker.Mask(line);
            ConsoleLogger.WriteJobOutput(jobId, maskedLine, isError);
            _outputObserver?.Invoke(maskedLine, isError);
            await AppendLogLineAsync(logWriter, logLock, maskedLine, cancellationToken);
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
