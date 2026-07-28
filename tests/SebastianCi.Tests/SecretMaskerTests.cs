using SebastianCi.Core;

namespace SebastianCi.Tests;

// SecretMasker はプロセス全体で共有されるため、干渉を避けて直列に実行する
[Collection("SecretMasker")]
public sealed class SecretMaskerTests : IDisposable
{
    public SecretMaskerTests() => SecretMasker.Clear();

    public void Dispose() => SecretMasker.Clear();

    [Fact]
    public void Mask_ReplacesRegisteredSecret()
    {
        SecretMasker.Register("super-secret-token-xyz");

        string masked = SecretMasker.Mask("curl -H 'Authorization: super-secret-token-xyz'");

        Assert.Equal($"curl -H 'Authorization: {SecretMasker.MaskText}'", masked);
    }

    [Fact]
    public void Mask_ReplacesAllOccurrencesOfMultipleSecrets()
    {
        SecretMasker.Register("secret-one");
        SecretMasker.Register("secret-two");

        string masked = SecretMasker.Mask("secret-one secret-two secret-one");

        Assert.DoesNotContain("secret-one", masked);
        Assert.DoesNotContain("secret-two", masked);
    }

    [Fact]
    public void Mask_PrefersLongerSecretWhenOverlapping()
    {
        SecretMasker.Register("token");
        SecretMasker.Register("token-with-suffix");

        string masked = SecretMasker.Mask("value=token-with-suffix");

        Assert.Equal($"value={SecretMasker.MaskText}", masked);
    }

    [Fact]
    public void Register_IgnoresShortValues()
    {
        SecretMasker.Register("abc");

        Assert.Equal("abc is fine", SecretMasker.Mask("abc is fine"));
    }

    [Fact]
    public void Register_IgnoresEmptyValues()
    {
        SecretMasker.Register("");
        SecretMasker.Register("   ");

        Assert.Equal("unchanged", SecretMasker.Mask("unchanged"));
    }

    [Fact]
    public void Mask_ReturnsLineUnchangedWithoutSecrets()
        => Assert.Equal("plain output", SecretMasker.Mask("plain output"));

    [Fact]
    public void Expand_RegistersResolvedHostValueForMasking()
    {
        string variableName = $"SEBASTIAN_CI_MASK_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "host-provided-secret-value");
        try
        {
            EnvironmentVariableExpander.Expand($"${variableName}", "test");

            Assert.Equal(SecretMasker.MaskText, SecretMasker.Mask("host-provided-secret-value"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }
}
