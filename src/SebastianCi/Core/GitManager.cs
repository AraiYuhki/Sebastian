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

    /// <summary>
    /// 指定コミット2点間の変更を分類して返す（差分転送用）。
    /// Changed は追加・変更されたパス、Deleted は削除されたパス。
    /// </summary>
    public async Task<(IReadOnlyList<string> Changed, IReadOnlyList<string> Deleted)> GetDiffStatusAsync(
        string baseCommitHash, string targetCommitHash, CancellationToken cancellationToken = default)
    {
        string output = await RunGitCommandAsync(
            $"diff --name-status {baseCommitHash} {targetCommitHash}", cancellationToken);
        List<string> changed = new();
        List<string> deleted = new();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ClassifyDiffLine(line, changed, deleted);
        }

        return (changed, deleted);
    }

    private static void ClassifyDiffLine(string line, List<string> changed, List<string> deleted)
    {
        string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        // 状態文字の先頭（A/M/D/R…）で分類。R（リネーム）は最後の新パスを変更として扱う
        char status = parts[0][0];
        if (status == 'D') deleted.Add(parts[1]);
        else changed.Add(parts[^1]);
    }

    /// <summary>指定コミットのツリーを tar 形式でアーカイブし、そのバイト列を返す（エージェントへの転送用）。</summary>
    public async Task<byte[]> CreateArchiveAsync(
        string commitHash, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default)
    {
        string pathArguments = paths is { Count: > 0 } ? " -- " + string.Join(' ', paths.Select(Quote)) : "";
        ProcessStartInfo startInfo = new()
        {
            FileName = GitExecutable,
            Arguments = $"archive --format=tar {commitHash}{pathArguments}",
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

    private static string Quote(string path) => $"\"{path}\"";

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
