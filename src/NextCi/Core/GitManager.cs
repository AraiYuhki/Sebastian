using System.Diagnostics;

namespace NextCi.Core;

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

        using Process process = Process.Start(startInfo)
            ?? throw new GitCommandException("git プロセスの起動に失敗しました。git がインストールされているか確認してください。");

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
}
