using SebastianCi.Models;

namespace SebastianCi.Core;

/// <summary>
/// ジョブの script と afterScript を、1つのシェルコマンド文字列へ組み立てる処理だけを担当する。
/// script は && でつなぎ（失敗で打ち切り）、afterScript は成否に関わらず全行を実行したうえで、
/// script の終了コードをそのまま返す（後処理の失敗はジョブの成否に影響しない）。
/// </summary>
public static class ScriptComposer
{
    /// <summary>
    /// POSIX sh（コンテナ内・Linux / macOS ホスト）向けのコマンド文字列を組み立てる。
    /// script は「exit で打ち切られても afterScript が走る」ようサブシェルで実行する。
    /// </summary>
    public static string ComposePosix(JobDefinition job)
    {
        string script = JoinPosix(job.Script, " && ");
        if (job.AfterScript.Count == 0) return script;

        string afterScript = JoinPosix(job.AfterScript, " ; ");
        // 複数行のスクリプト本文（ヒアドキュメントや末尾のコメントを含みうる）でも ")" が本文に巻き込まれないよう、改行してから閉じる。
        string close = script.Contains('\n') ? "\n)" : " )";
        return $"( {script}{close}; __sebastian_ci_exit=$?; {afterScript} ; exit $__sebastian_ci_exit";
    }

    /// <summary>
    /// コマンドを区切り文字でつなぐ。複数行のコマンド（スクリプト本文）は { ... } のグループに包み、
    /// 前後のコマンドとつないでも、本文の途中の改行・ヒアドキュメント・コメントが壊れないようにする。
    /// 本文が1件だけで他に何もつながないときは、そのまま返す。
    /// </summary>
    private static string JoinPosix(IReadOnlyList<string> commands, string separator)
    {
        if (commands.Count == 1) return commands[0].TrimEnd('\r', '\n');

        return string.Join(separator, commands.Select(command =>
            command.Contains('\n') ? "{\n" + command.TrimEnd('\r', '\n') + "\n}" : command));
    }

    /// <summary>
    /// cmd.exe（Windows ホストの shell ジョブ）向けのコマンド文字列を組み立てる。
    /// afterScript がある場合は遅延展開（/v:on）が必要（<see cref="RequiresDelayedExpansion"/>）。
    /// </summary>
    public static string ComposeWindows(JobDefinition job)
    {
        if (job.Script.Concat(job.AfterScript).Any(command => command.Contains('\n')))
        {
            throw new InvalidPipelineException("複数行のスクリプト本文は、Windows のホスト直接実行（cmd.exe）では使えません。コンテナ、または Linux / macOS のホストで実行してください。");
        }

        string script = string.Join(" && ", job.Script);
        if (job.AfterScript.Count == 0) return script;

        string afterScript = string.Join(" & ", job.AfterScript);
        return $"({script}) & set __SEBASTIAN_CI_EXIT=!ERRORLEVEL! & {afterScript} & exit /b !__SEBASTIAN_CI_EXIT!";
    }

    /// <summary>Windows で afterScript を使う場合、cmd.exe に /v:on（遅延展開）が必要かどうか。</summary>
    public static bool RequiresDelayedExpansion(JobDefinition job) => job.AfterScript.Count > 0;
}
