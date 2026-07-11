using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブが宣言した成果物（Artifacts）の
/// .sebastian-ci/artifacts/[commit_hash]/[job_id]/ への退避だけを担当する。
/// </summary>
public sealed class ArtifactManager
{
    private const string ArtifactsDirectoryName = "artifacts";

    private readonly string _workspacePath;
    private readonly string _artifactsRootPath;

    public ArtifactManager(string workspacePath, string dataRootPath, string commitHash)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _artifactsRootPath = Path.Combine(dataRootPath, ArtifactsDirectoryName, commitHash);
    }

    /// <summary>
    /// ジョブ成功後に呼び出し、artifacts に列挙されたファイル・ディレクトリを退避する。
    /// 見つからないパスは警告を出してスキップする（ジョブ自体は成功扱いのまま）。
    /// </summary>
    public async Task CollectAsync(string jobId, JobDefinition job, CancellationToken cancellationToken = default)
    {
        if (job.Artifacts.Count == 0) return;

        string destinationRootPath = Path.Combine(_artifactsRootPath, jobId);
        Directory.CreateDirectory(destinationRootPath);

        foreach (string artifactPath in job.Artifacts)
        {
            await CollectSingleAsync(jobId, artifactPath, destinationRootPath, cancellationToken);
        }

        ConsoleLogger.WriteInfo($"📦 ジョブ '{jobId}' の成果物を保存しました: {destinationRootPath}");
    }

    private async Task CollectSingleAsync(
        string jobId, string artifactPath, string destinationRootPath, CancellationToken cancellationToken)
    {
        string sourcePath = ResolveWorkspacePath(jobId, artifactPath);
        string destinationPath = Path.Combine(destinationRootPath, artifactPath);

        if (Directory.Exists(sourcePath))
        {
            await CopyDirectoryAsync(sourcePath, destinationPath, cancellationToken);
            return;
        }

        if (File.Exists(sourcePath))
        {
            await CopyFileAsync(sourcePath, destinationPath, cancellationToken);
            return;
        }

        ConsoleLogger.WriteWarning($"⚠ ジョブ '{jobId}' の成果物 '{artifactPath}' が見つからないためスキップします。");
    }

    private string ResolveWorkspacePath(string jobId, string artifactPath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_workspacePath, artifactPath));
        if (!fullPath.StartsWith(_workspacePath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidPipelineException(
                $"ジョブ '{jobId}' の artifacts '{artifactPath}' がリポジトリの外を指しています。");
        }

        return fullPath;
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectoryPath, string destinationDirectoryPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectoryPath);
        foreach (string sourceFilePath in
            Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, sourceFilePath);
            await CopyFileAsync(sourceFilePath, Path.Combine(destinationDirectoryPath, relativePath), cancellationToken);
        }
    }

    private static async Task CopyFileAsync(
        string sourceFilePath, string destinationFilePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        await using FileStream sourceStream = File.OpenRead(sourceFilePath);
        await using FileStream destinationStream = File.Create(destinationFilePath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }
}
