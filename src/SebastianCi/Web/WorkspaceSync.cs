using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SebastianCi.Web;

/// <summary>ワークスペース型ジョブが取り込む Git ソース1件。Directory はワークスペース直下の配置先（"." は単一ソースのときのみ）。</summary>
public sealed record SourceEntry(string Url, string? Branch, string Directory);

/// <summary>
/// ワークスペース型ジョブの実行直前に、登録されたソース（Git リポジトリ）をワークスペースへ clone / 更新する。
/// 認証情報は URL に埋め込ませず、このマシンの git の設定（credential helper・SSH 鍵）を使う。
/// 出力は実行ログへ1行ずつ流す（認証情報は伏せる）。
/// </summary>
public static class WorkspaceSync
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(15);

    /// <summary>git に許す通信方式（GIT_ALLOW_PROTOCOL）。登録時の URL 検証と合わせて https / ssh に限る。テストでのみ変更する。</summary>
    public static string AllowedProtocols { get; set; } = "https:ssh";

    /// <summary>
    /// 全ソースを最新にして、この実行を表す擬似リビジョン（40桁の16進）を返す。失敗したら理由をログに出して null。
    /// リビジョンは「各ソースの HEAD + 実行時刻」から作るため、実行ごとに異なり、履歴に実行1回ずつが残る。
    /// </summary>
    public static async Task<string?> PrepareAsync(
        string workspacePath, IReadOnlyList<SourceEntry> sources, Action<string> log, CancellationToken cancellationToken)
    {
        StringBuilder seed = new();
        try
        {
            Directory.CreateDirectory(workspacePath);
            foreach (SourceEntry source in sources)
            {
                string? head = await SyncOneAsync(workspacePath, source, log, cancellationToken);
                if (head is null) return null;
                seed.Append(source.Directory).Append('=').Append(head).Append(';');
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"💥 ワークスペースを準備できません: {exception.Message}");
            return null;
        }

        seed.Append(DateTime.UtcNow.Ticks).Append(';').Append(Guid.NewGuid());
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(seed.ToString()))).ToLowerInvariant();
    }

    /// <summary>ソースを配置先の絶対パスにする。"." はワークスペース自身。</summary>
    public static string ResolveTarget(string workspacePath, SourceEntry source)
        => source.Directory == "." ? workspacePath : Path.Combine(workspacePath, source.Directory);

    private static async Task<string?> SyncOneAsync(
        string workspacePath, SourceEntry source, Action<string> log, CancellationToken cancellationToken)
    {
        string target = ResolveTarget(workspacePath, source);
        string label = GitCloner.MaskCredentials(source.Url);
        log($"📥 ソースを取得します: {label}{(source.Branch is null ? "" : $" ({source.Branch})")} → {source.Directory}");

        bool isRepository = Directory.Exists(Path.Combine(target, ".git")) || File.Exists(Path.Combine(target, ".git"));
        if (!isRepository)
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                log($"💥 配置先「{source.Directory}」は空ではなく、Git リポジトリでもありません。");
                return null;
            }

            Directory.CreateDirectory(target);
            List<string> clone = ["clone"];
            if (source.Branch is not null) clone.AddRange(["-b", source.Branch]);
            clone.AddRange(["--", source.Url, target]);
            if (!await RunGitAsync(workspacePath, clone, log, cancellationToken)) return null;
        }
        else
        {
            if (!await RunGitAsync(target, ["remote", "set-url", "origin", source.Url], log, cancellationToken)
                || !await RunGitAsync(target, ["fetch", "--prune", "origin"], log, cancellationToken))
            {
                return null;
            }

            if (source.Branch is not null)
            {
                if (!await RunGitAsync(target, ["checkout", "-f", "-B", source.Branch, $"origin/{source.Branch}"], log, cancellationToken)) return null;
            }
            else
            {
                // リモートの既定ブランチが変わっていても追随する（失敗しても続行し、現在の origin/HEAD を使う）。
                await RunGitAsync(target, ["remote", "set-head", "origin", "-a"], _ => { }, cancellationToken);
                if (!await RunGitAsync(target, ["reset", "--hard", "origin/HEAD"], log, cancellationToken)) return null;
            }
        }

        (bool ok, string output) = await RunGitCaptureAsync(target, ["rev-parse", "HEAD"], cancellationToken);
        if (!ok)
        {
            log($"💥 取得したソースのコミットを確認できません: {output}");
            return null;
        }

        log($"✅ {source.Directory}: {output.Trim()[..Math.Min(8, output.Trim().Length)]}");
        return output.Trim();
    }

    private static async Task<bool> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, Action<string> log, CancellationToken cancellationToken)
    {
        (bool ok, string output) = await RunGitCaptureAsync(workingDirectory, arguments, cancellationToken);
        // "Cloning into '<サーバー上のパス>'..." は、閲覧だけのユーザーにも見える実行ログに出さない。
        foreach (string line in output.Split('\n').Select(line => line.TrimEnd('\r'))
                     .Where(line => line.Length > 0 && !line.StartsWith("Cloning into", StringComparison.Ordinal)))
        {
            log("   " + line);
        }

        if (!ok) log("💥 git が失敗しました。");
        return ok;
    }

    /// <summary>git をシェルを介さず実行し、成否と（認証情報を伏せた）出力を返す。対話入力は無効にする。</summary>
    private static async Task<(bool Ok, string Output)> RunGitCaptureAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_ALLOW_PROTOCOL"] = AllowedProtocols;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
        {
            startInfo.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        StringBuilder output = new();
        using Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "git コマンドを実行できません。git がインストールされ PATH に通っているか確認してください。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (false, cancellationToken.IsCancellationRequested ? "中断しました。" : "git がタイムアウトしました。");
        }

        // WaitForExitAsync の後も非同期の出力読み取りが残ることがあるため、同期版の待機で出力を確定させる。
        process.WaitForExit();
        string text;
        lock (output) { text = GitCloner.MaskCredentials(output.ToString()); }
        return (process.ExitCode == 0, text);
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null) return;
        lock (output) { output.AppendLine(line); }
    }
}
