using SebastianCi.Core;

namespace SebastianCi.Tests;

public class EnvironmentVariableExpanderTests
{
    [Fact]
    public void Expand_ReplacesSimpleReference()
    {
        string variableName = UniqueVariableName();
        Environment.SetEnvironmentVariable(variableName, "secret");

        string result = EnvironmentVariableExpander.Expand($"${variableName}", "test");

        Assert.Equal("secret", result);
    }

    [Fact]
    public void Expand_ReplacesBracedReferenceEmbeddedInText()
    {
        string variableName = UniqueVariableName();
        Environment.SetEnvironmentVariable(variableName, "example.com");

        string result = EnvironmentVariableExpander.Expand($"https://${{{variableName}}}/api", "test");

        Assert.Equal("https://example.com/api", result);
    }

    [Fact]
    public void Expand_EscapesDoubleDollarToLiteralDollar()
    {
        string result = EnvironmentVariableExpander.Expand("costs $$5", "test");

        Assert.Equal("costs $5", result);
    }

    [Fact]
    public void Expand_ThrowsWhenReferencedVariableIsUndefined()
    {
        string variableName = UniqueVariableName();
        Environment.SetEnvironmentVariable(variableName, null);

        InvalidPipelineException exception = Assert.Throws<InvalidPipelineException>(
            () => EnvironmentVariableExpander.Expand($"${variableName}", "test"));
        Assert.Contains(variableName, exception.Message);
    }

    [Fact]
    public void Expand_LeavesPlainTextUntouched()
        => Assert.Equal("plain value", EnvironmentVariableExpander.Expand("plain value", "test"));

    private static string UniqueVariableName() => $"SEBASTIAN_CI_TEST_{Guid.NewGuid():N}";
}
