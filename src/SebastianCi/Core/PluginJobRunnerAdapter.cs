using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// プラグインの <see cref="IPluginJobRunner"/> を本体の <see cref="IJobRunner"/> として扱う橋渡し。
/// 出力をコンソール表示＋ログ保存へ流し、非0終了は失敗として例外化する。
/// </summary>
public sealed class PluginJobRunnerAdapter : IJobRunner
{
    private readonly IPluginJobRunner _pluginRunner;
    private readonly string _workspacePath;
    private readonly string _logDirectoryPath;

    public PluginJobRunnerAdapter(IPluginJobRunner pluginRunner, string workspacePath, string logDirectoryPath)
    {
        _pluginRunner = pluginRunner;
        _workspacePath = workspacePath;
        _logDirectoryPath = logDirectoryPath;
    }

    public async Task RunJobAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        ConsoleLogger.WriteInfo($"🧩 ジョブ '{jobId}' をプラグインランナー '{_pluginRunner.Name}' で実行します");
        List<string> captured = new();
        PluginJobContext context = new(
            jobId, job.Image, job.Script, job.Env, _workspacePath,
            (line, isError) => Capture(jobId, line, isError, captured));

        int exitCode = await RunPluginAsync(jobId, context, cancellationToken);
        await WriteLogAsync(jobId, captured, cancellationToken);

        if (exitCode != 0)
        {
            throw new ContainerExecutionException(
                $"プラグインランナー '{_pluginRunner.Name}' 上でジョブ '{jobId}' が終了コード {exitCode} で失敗しました。");
        }
    }

    private async Task<int> RunPluginAsync(string jobId, PluginJobContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await _pluginRunner.RunAsync(context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ContainerExecutionException(
                $"プラグインランナー '{_pluginRunner.Name}' がジョブ '{jobId}' の実行中に例外を投げました: {exception.Message}");
        }
    }

    private static void Capture(string jobId, string line, bool isError, List<string> captured)
    {
        ConsoleLogger.WriteJobOutput(jobId, line, isError);
        lock (captured)
        {
            captured.Add(line);
        }
    }

    private async Task WriteLogAsync(string jobId, List<string> captured, CancellationToken cancellationToken)
    {
        string logPath = Path.Combine(_logDirectoryPath, $"{PathSanitizer.ToFileSystemName(jobId)}.log");
        await File.WriteAllLinesAsync(logPath, captured, cancellationToken);
    }
}
