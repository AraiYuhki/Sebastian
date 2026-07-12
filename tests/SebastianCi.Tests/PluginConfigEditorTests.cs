using SebastianCi.Core;
using SebastianCi.Web;

namespace SebastianCi.Tests;

public class PluginConfigEditorTests
{
    private const string BaseYaml = """
        # コメントは保持される
        image: alpine
        jobs:
          ok: { script: ["echo hi"] }
        """;

    [Fact]
    public void AddPath_AppendsPluginsSectionWhenMissing()
    {
        string result = PluginConfigEditor.AddPath(BaseYaml, "./plugins/My.dll");

        Assert.Contains("plugins:", result);
        Assert.Contains("  - path: ./plugins/My.dll", result);
        Assert.Contains("# コメントは保持される", result);
    }

    [Fact]
    public void AddPackage_InsertsIntoExistingSection()
    {
        string yaml = PluginConfigEditor.AddPath(BaseYaml, "./a.dll");

        string result = PluginConfigEditor.AddPackage(yaml, "My.Package", "1.2.3");

        Assert.Contains("  - package: My.Package", result);
        Assert.Contains("    version: 1.2.3", result);
        Assert.Contains("  - path: ./a.dll", result);
        Assert.Single(result.Split('\n').Where(line => line.StartsWith("plugins:")));
    }

    [Fact]
    public void AddPackage_OmitsVersionLineWhenEmpty()
    {
        string result = PluginConfigEditor.AddPackage(BaseYaml, "My.Package", "");

        Assert.Contains("  - package: My.Package", result);
        Assert.DoesNotContain("version:", result);
    }

    [Fact]
    public void Remove_DeletesMatchingEntryAndKeepsOthers()
    {
        string yaml = PluginConfigEditor.AddPackage(
            PluginConfigEditor.AddPath(BaseYaml, "./a.dll"), "My.Package", "1.0.0");

        string result = PluginConfigEditor.Remove(yaml, "My.Package");

        Assert.DoesNotContain("My.Package", result);
        Assert.DoesNotContain("version: 1.0.0", result);
        Assert.Contains("  - path: ./a.dll", result);
    }

    [Fact]
    public void Remove_LastEntryRemovesSectionHeader()
    {
        string yaml = PluginConfigEditor.AddPath(BaseYaml, "./a.dll");

        string result = PluginConfigEditor.Remove(yaml, "./a.dll");

        Assert.DoesNotContain("plugins:", result);
        Assert.Contains("image: alpine", result);
    }

    [Fact]
    public void Remove_UnknownIdentifierLeavesYamlUnchanged()
    {
        string yaml = PluginConfigEditor.AddPath(BaseYaml, "./a.dll");

        Assert.Equal(yaml, PluginConfigEditor.Remove(yaml, "ghost"));
    }

    [Fact]
    public void AddPath_ThrowsForInlineFlowStyle()
    {
        string yaml = BaseYaml + "\nplugins: []\n";

        Assert.Throws<PluginException>(() => PluginConfigEditor.AddPath(yaml, "./a.dll"));
    }
}
