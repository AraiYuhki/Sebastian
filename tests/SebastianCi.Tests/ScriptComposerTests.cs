using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public sealed class ScriptComposerTests
{
    [Fact]
    public void ComposePosix_JoinsScriptWithAndOperatorWhenNoAfterScript()
    {
        JobDefinition job = new() { Script = ["echo a", "echo b"] };

        Assert.Equal("echo a && echo b", ScriptComposer.ComposePosix(job));
    }

    [Fact]
    public void ComposePosix_PreservesScriptExitCodeAroundAfterScript()
    {
        JobDefinition job = new()
        {
            Script = ["echo a", "echo b"],
            AfterScript = ["rm -f temp.txt", "echo done"]
        };

        Assert.Equal(
            "( echo a && echo b ); __sebastian_ci_exit=$?; rm -f temp.txt ; echo done ; exit $__sebastian_ci_exit",
            ScriptComposer.ComposePosix(job));
    }

    [Fact]
    public void ComposeWindows_JoinsScriptWithAndOperatorWhenNoAfterScript()
    {
        JobDefinition job = new() { Script = ["echo a", "echo b"] };

        Assert.Equal("echo a && echo b", ScriptComposer.ComposeWindows(job));
    }

    [Fact]
    public void ComposeWindows_PreservesScriptExitCodeAroundAfterScript()
    {
        JobDefinition job = new() { Script = ["build"], AfterScript = ["cleanup"] };

        Assert.Equal(
            "(build) & set __SEBASTIAN_CI_EXIT=!ERRORLEVEL! & cleanup & exit /b !__SEBASTIAN_CI_EXIT!",
            ScriptComposer.ComposeWindows(job));
    }

    [Fact]
    public void RequiresDelayedExpansion_OnlyWithAfterScript()
    {
        Assert.False(ScriptComposer.RequiresDelayedExpansion(new JobDefinition { Script = ["echo a"] }));
        Assert.True(ScriptComposer.RequiresDelayedExpansion(
            new JobDefinition { Script = ["echo a"], AfterScript = ["echo b"] }));
    }
}
