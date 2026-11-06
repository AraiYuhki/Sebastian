using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SebastianCi.Web;

/// <summary>
/// ハブから `git clone` を行う。URL は https / ssh（scp 形式含む）に限定し、
/// 認証情報を URL に埋め込ませない（hub.json に秘密が残らないよう、credential helper や SSH 鍵を使わせる）。
/// 引数はシェルを介さず渡し、プロンプト待ちで固まらないよう対話入力を無効化する。
/// </summary>
public static class GitCloner
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(15);
    private static readonly Regex ScpLikePattern = new(
        @"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._~/%+-]+$", RegexOptions.Compiled);
    private static readonly Regex UrlCredentialPattern = new(@"(?<=://)[^/@\s]+@", RegexOptions.Compiled);

    /// <summary>問題なければ null、あればその理由を返す。</summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Git の URL を入力してください。";

        string trimmed = url.Trim();
        if (trimmed.Length > 500 || trimmed.StartsWith('-') || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "URL の形式が正しくありません。";
        }

        if (ScpLikePattern.IsMatch(trimmed)) return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            return "URL は https://…、ssh://…、または git@host:path の形式で指定してください。";
        }

        if (uri.Scheme is not ("https" or "ssh"))
        {
            return "使えるのは https:// と ssh:// の URL だけです。";
        }

        return uri.UserInfo.Contains(':')
            ? "URL にパスワードやトークンを含めないでください（git の credential helper や SSH 鍵を使ってください）。"
            : null;
    }

    /// <summary>出力に混ざりうる URL 内の認証情報（user:pass@）を伏せる。</summary>
    public static string MaskCredentials(string text) => UrlCredentialPattern.Replace(text, "***@");

    /// <summary>url を destination に clone する。成否と（認証情報を伏せた）出力の末尾を返す。</summary>
    public static async Task<(bool Success, string Output)> CloneAsync(
        string url, string destination, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "clone", "--", url.Trim(), destination }) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_ALLOW_PROTOCOL"] = "https:ssh";
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
        timeout.CancelAfter(CloneTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (false, cancellationToken.IsCancellationRequested ? "クローンを中断しました。" : "クローンがタイムアウトしました。");
        }

        // "Cloning into '<サーバー上のパス>'..." は一般ユーザーにも表示されるエラーに混ぜない。
        string text = MaskCredentials(string.Join(
            Environment.NewLine,
            output.ToString().Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => !line.StartsWith("Cloning into", StringComparison.Ordinal))).Trim());
        if (text.Length > 2000) text = "…" + text[^2000..];
        return (process.ExitCode == 0, text);
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null) return;
        lock (output) { output.AppendLine(line); }
    }
}
