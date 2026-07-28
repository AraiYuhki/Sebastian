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
        string script = string.Join(" && ", job.Script);
        if (job.AfterScript.Count == 0) return script;

        string afterScript = string.Join(" ; ", job.AfterScript);
        return $"( {script} ); __sebastian_ci_exit=$?; {afterScript} ; exit $__sebastian_ci_exit";
    }

    /// <summary>
    /// cmd.exe（Windows ホストの shell ジョブ）向けのコマンド文字列を組み立てる。
    /// afterScript がある場合は遅延展開（/v:on）が必要（<see cref="RequiresDelayedExpansion"/>）。
    /// </summary>
    public static string ComposeWindows(JobDefinition job)
    {
        string script = string.Join(" && ", job.Script);
        if (job.AfterScript.Count == 0) return script;

        string afterScript = string.Join(" & ", job.AfterScript);
        return $"({script}) & set __SEBASTIAN_CI_EXIT=!ERRORLEVEL! & {afterScript} & exit /b !__SEBASTIAN_CI_EXIT!";
    }

    /// <summary>Windows で afterScript を使う場合、cmd.exe に /v:on（遅延展開）が必要かどうか。</summary>
    public static bool RequiresDelayedExpansion(JobDefinition job) => job.AfterScript.Count > 0;
}
