using System.ComponentModel;
using System.Diagnostics;

namespace SebastianCi.Core;

/// <summary>
/// git コマンドの制御（コミットハッシュ取得・変更検知）だけを担当する。
/// </summary>
public sealed class GitManager
{
    private const string GitExecutable = "git";

    private readonly string _repositoryPath;

    public GitManager(string repositoryPath) => _repositoryPath = repositoryPath;

    /// <summary>HEAD のコミットハッシュ（40桁）を取得する。</summary>
    public async Task<string> GetCurrentCommitHashAsync(CancellationToken cancellationToken = default)
        => (await RunGitCommandAsync("rev-parse HEAD", cancellationToken)).Trim();

    /// <summary>未コミットの変更（ワーキングツリーの差分）が存在するかを検知する。</summary>
    public async Task<bool> HasUncommittedChangesAsync(CancellationToken cancellationToken = default)
        => !string.IsNullOrWhiteSpace(await RunGitCommandAsync("status --porcelain", cancellationToken));

    /// <summary>指定コミットから HEAD までに変更されたファイルパスの一覧を取得する。</summary>
    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(
        string fromCommitHash, CancellationToken cancellationToken = default)
    {
        string output = await RunGitCommandAsync($"diff --name-only {fromCommitHash} HEAD", cancellationToken);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>指定コミットのツリーを tar 形式でアーカイブし、そのバイト列を返す（エージェントへの転送用）。</summary>
    public async Task<byte[]> CreateArchiveAsync(string commitHash, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = GitExecutable,
            Arguments = $"archive --format=tar {commitHash}",
            WorkingDirectory = _repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = StartGitProcess(startInfo);
        using MemoryStream buffer = new();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new GitCommandException($"git archive が失敗しました: {standardError.Trim()}");
        }

        return buffer.ToArray();
    }

    private async Task<string> RunGitCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = GitExecutable,
            Arguments = arguments,
            WorkingDirectory = _repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = StartGitProcess(startInfo);
        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new GitCommandException(
                $"'git {arguments}' が終了コード {process.ExitCode} で失敗しました: {standardError.Trim()}");
        }

        return standardOutput;
    }

    private static Process StartGitProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new GitCommandException("git プロセスの起動に失敗しました。");
        }
        catch (Win32Exception exception)
        {
            throw new GitCommandException(
                $"git を起動できません。git がインストールされているか確認してください: {exception.Message}");
        }
    }
}
