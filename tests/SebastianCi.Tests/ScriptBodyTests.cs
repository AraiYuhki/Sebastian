using System.Diagnostics;
using SebastianCi.Core;
using SebastianCi.Models;
using SebastianCi.Web;

namespace SebastianCi.Tests;

/// <summary>複数行のスクリプト本文（1件の複数行コマンド）の保存・読み出し・組み立てのテスト。</summary>
public sealed class ScriptBodyTests
{
    private const string Body = "#!/bin/sh\nset -e\n\nfor f in a b; do\n  echo \"$f\"\ndone\ncat <<EOF\nhello\nEOF\n# 末尾のコメント";

    private static JobFormPayload Payload(List<string> script)
        => new(null, "build", "alpine:3.20", null, null, script, null, null, null, null, null, null);

    // ---- YAML への保存と読み出し ----

    [Fact]
    public void Upsert_WritesMultilineScriptAsLiteralBlockAndReadsItBackUnchanged()
    {
        string yaml = JobConfigEditor.Upsert("name: p\n", Payload([Body]));

        Assert.Contains("- |", yaml); // "\n" だらけの引用符つきではなく、読みやすいリテラルブロック
        Assert.DoesNotContain(@"\n", yaml);
        string readBack = JobConfigEditor.Read(yaml).Jobs.Single().Script.Single();
        Assert.Equal(Body, readBack.TrimEnd('\n'));
    }

    [Fact]
    public void Upsert_KeepsSingleLineCommandsAsPlainItems()
    {
        string yaml = JobConfigEditor.Upsert("name: p\n", Payload(["echo a", "echo b"]));

        Assert.DoesNotContain("|", yaml);
        Assert.Equal(["echo a", "echo b"], JobConfigEditor.Read(yaml).Jobs.Single().Script);
    }

    [Fact]
    public void Upsert_EditingAnotherJobLeavesTheLiteralBlockIntact()
    {
        string first = JobConfigEditor.Upsert("name: p\n", Payload([Body]));
        string second = JobConfigEditor.Upsert(first, new JobFormPayload(null, "other", "alpine:3.20", null, null, ["echo x"], null, null, null, null, null, null));

        Assert.Equal(Body, JobConfigEditor.Read(second).Jobs.Single(j => j.Name == "build").Script.Single().TrimEnd('\n'));
        Assert.Equal(["echo x"], JobConfigEditor.Read(second).Jobs.Single(j => j.Name == "other").Script);
    }

    [Fact]
    public void Upsert_HandlesBlankLinesAndIndentedFirstLine()
    {
        string tricky = "  indented first\n\n\nlast";
        string yaml = JobConfigEditor.Upsert("name: p\n", Payload([tricky]));

        Assert.Equal(tricky, JobConfigEditor.Read(yaml).Jobs.Single().Script.Single().TrimEnd('\n'));
    }

    // ---- 実行用の文字列への組み立て ----

    [Fact]
    public void ComposePosix_SingleMultilineBodyIsPassedThroughAsIs()
        => Assert.Equal(Body, ScriptComposer.ComposePosix(new JobDefinition { Script = [Body + "\n"] }));

    [Fact]
    public void ComposePosix_WrapsMultilineItemsInGroupsWhenJoined()
    {
        JobDefinition job = new() { Script = ["echo before", "if true; then\n  echo yes\nfi", "echo after"] };

        Assert.Equal("echo before && {\nif true; then\n  echo yes\nfi\n} && echo after", ScriptComposer.ComposePosix(job));
    }

    [Fact]
    public void ComposePosix_ClosesTheSubshellOnANewLineAfterAMultilineBody()
    {
        JobDefinition job = new() { Script = [Body], AfterScript = ["echo cleanup"] };

        string composed = ScriptComposer.ComposePosix(job);

        // 本文の最後がコメントでも、")" が本文に巻き込まれない
        Assert.Contains("# 末尾のコメント\n); __sebastian_ci_exit=$?; echo cleanup ; exit $__sebastian_ci_exit", composed);
    }

    [Fact]
    public void ComposePosix_SingleLineBehaviorIsUnchanged()
    {
        JobDefinition job = new() { Script = ["echo a", "echo b"], AfterScript = ["echo done"] };

        Assert.Equal(
            "( echo a && echo b ); __sebastian_ci_exit=$?; echo done ; exit $__sebastian_ci_exit",
            ScriptComposer.ComposePosix(job));
    }

    [Fact]
    public void ComposeWindows_RejectsMultilineBodies()
        => Assert.Throws<InvalidPipelineException>(() => ScriptComposer.ComposeWindows(new JobDefinition { Script = ["a\nb"] }));

    // ---- 実際の sh での実行（sh がある環境のみ） ----

    private static string? FindSh()
        => new[] { "/bin/sh", "/usr/bin/sh", @"C:\Program Files\Git\usr\bin\sh.exe", @"C:\Program Files\Git\bin\sh.exe" }
            .FirstOrDefault(File.Exists);

    [Fact]
    public void ComposedScript_RunsCorrectlyInRealShell()
    {
        string? sh = FindSh();
        if (sh is null) return; // sh が無い環境（素の Windows など）では実行までは確認しない

        JobDefinition job = new()
        {
            Script = ["echo start", "for f in a b; do\n  echo \"item-$f\"\ndone\ncat <<'EOT'\nheredoc-line\nEOT\n# trailing comment"],
            AfterScript = ["echo cleanup"]
        };
        ProcessStartInfo info = new(sh) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(ScriptComposer.ComposePosix(job));
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.Equal("start\nitem-a\nitem-b\nheredoc-line\ncleanup\n", output);
    }

    [Fact]
    public void ComposedScript_PropagatesFailureExitCodeAfterRunningAfterScript()
    {
        string? sh = FindSh();
        if (sh is null) return;

        JobDefinition job = new() { Script = ["echo go\nfalse\necho unreachable-not-guaranteed-without-set-e"], AfterScript = ["echo cleanup"] };
        job.Script[0] = "set -e\necho go\nfalse\necho never";
        ProcessStartInfo info = new(sh) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(ScriptComposer.ComposePosix(job));
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal("go\ncleanup\n", output); // set -e で止まり、後処理は実行され、終了コードは失敗のまま
    }
}
