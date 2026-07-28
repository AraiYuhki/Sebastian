using System.ComponentModel;
using System.Diagnostics;
using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// コンテナを使わず、ホストマシン上でジョブの script を直接実行する（shell: true）。
/// Xcode のようにコンテナ化できないツールチェーン（macOS / iOS ビルド等）を扱うためのランナー。
/// ワークスペースを作業ディレクトリとし、ホストの環境変数を引き継いだうえでジョブの env を上書き適用する。
/// コンテナと違いプロセスが残留し得るため、タイムアウト・中断時はプロセスツリーごと停止する。
/// </summary>
public sealed class ShellRunner : IJobRunner
{
    private readonly string _workspacePath;
    private readonly string _logDirectoryPath;
    private readonly Action<string, bool>? _outputObserver;

    public ShellRunner(string workspacePath, string logDirectoryPath, Action<string, bool>? outputObserver = null)
    {
        _workspacePath = ResolveWorkspacePath(workspacePath);
        _logDirectoryPath = logDirectoryPath;
        _outputObserver = outputObserver;
    }

    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        string logFilePath = Path.Combine(_logDirectoryPath, $"{PathSanitizer.ToFileSystemName(jobId)}.log");
        await using StreamWriter logWriter = new(logFilePath, append: false);

        using Process process = new() { StartInfo = BuildStartInfo(job) };
        StartProcess(process, jobId);
        await AwaitCompletionAsync(process, job, jobId, logWriter, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new ContainerExecutionException(
                $"シェル実行が終了コード {process.ExitCode} で異常終了しました (ジョブ: {jobId}, ログ: {logFilePath})");
        }
    }

    /// <summary>
    /// 出力の回収とプロセス終了を待つ。タイムアウト超過ならプロセスツリーを停止して失敗扱いに、
    /// 中断（Ctrl+C）ならプロセスツリーを停止したうえでキャンセルをそのまま伝播する。
    /// </summary>
    private async Task AwaitCompletionAsync(
        Process process, JobDefinition job, string jobId,
        StreamWriter logWriter, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CreateTimeoutSource(job, cancellationToken);
        try
        {
            await StreamOutputAsync(process, jobId, logWriter, timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            await logWriter.FlushAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw new ContainerExecutionException(
                $"ジョブ '{jobId}' が制限時間 {job.Timeout} 秒を超過したため中断しました。");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static CancellationTokenSource CreateTimeoutSource(JobDefinition job, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (job.Timeout > 0) source.CancelAfter(TimeSpan.FromSeconds(job.Timeout));
        return source;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // 停止指示と自然終了が競合した場合は、すでに終了しているため何もしない
        }
    }

    private void StartProcess(Process process, string jobId)
    {
        try
        {
            if (!process.Start())
            {
                throw new ContainerExecutionException(
                    $"{ShellExecutable} プロセスの起動に失敗しました (ジョブ: {jobId})");
            }
        }
        catch (Win32Exception exception)
        {
            throw new ContainerExecutionException(
                $"{ShellExecutable} を起動できません (ジョブ: {jobId}): {exception.Message}");
        }
    }

    private ProcessStartInfo BuildStartInfo(JobDefinition job)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ShellExecutable,
            WorkingDirectory = _workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(ShellCommandFlag);
        startInfo.ArgumentList.Add(string.Join(" && ", job.Script));

        foreach ((string key, string value) in job.Env)
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private static string ShellExecutable => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static string ShellCommandFlag => OperatingSystem.IsWindows() ? "/c" : "-c";

    private static string ResolveWorkspacePath(string workspacePath)
    {
        string fullPath = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(fullPath))
        {
            throw new ContainerExecutionException($"実行対象のワークスペースが存在しません: {fullPath}");
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
