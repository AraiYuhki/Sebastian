using SebastianCi.Web;

namespace SebastianCi.Tests;

public class JobTemplateCatalogTests
{
    [Fact]
    public void Templates_ContainAllThreeEngines()
    {
        Assert.Equal(["godot", "unity", "unreal"], JobTemplateCatalog.Templates.Select(t => t.Id));
    }

    [Fact]
    public void Instantiate_ThrowsOnUnknownId()
    {
        Assert.Throws<ArgumentException>(() => JobTemplateCatalog.Instantiate("cryengine", new Dictionary<string, object>()));
    }

    [Fact]
    public void Godot_EmbedsVersionPresetAndOutput()
    {
        (string name, Dictionary<string, object> job) = JobTemplateCatalog.Instantiate("godot",
            new Dictionary<string, object>
            {
                ["jobName"] = "export", ["version"] = "4.3", ["preset"] = "Windows Desktop",
                ["output"] = "build/win/game.exe",
            });

        Assert.Equal("export", name);
        Assert.Equal("barichello/godot-ci:4.3", job["image"]);
        List<string> script = (List<string>)job["script"];
        Assert.Contains("mkdir -p build/win", script);
        Assert.Contains("godot --headless --export-release \"Windows Desktop\" build/win/game.exe", script);
        Assert.Equal(["build/win"], (List<string>)job["artifacts"]);
    }

    [Fact]
    public void Unity_UsesMatrixOnlyForMultipleTargets()
    {
        (_, Dictionary<string, object> multi) = JobTemplateCatalog.Instantiate("unity",
            new Dictionary<string, object> { ["targets"] = new List<string> { "WebGL", "Android" } });
        (_, Dictionary<string, object> single) = JobTemplateCatalog.Instantiate("unity",
            new Dictionary<string, object> { ["targets"] = new List<string> { "WebGL" } });

        Assert.True(multi.ContainsKey("matrix"));
        Assert.Contains("$target", ((List<string>)multi["script"]).Single());
        Assert.False(single.ContainsKey("matrix"));
        Assert.Contains("-buildTarget \"WebGL\"", ((List<string>)single["script"]).Single());
    }

    [Fact]
    public void Unreal_EmbedsProjectPlatformAndConfig()
    {
        (string name, Dictionary<string, object> job) = JobTemplateCatalog.Instantiate("unreal",
            new Dictionary<string, object>
            {
                ["version"] = "5.4", ["uproject"] = "Sample.uproject", ["platform"] = "Win64",
                ["config"] = "Shipping",
            });

        Assert.Equal("unreal-package", name);
        Assert.Equal("ghcr.io/epicgames/unreal-engine:dev-5.4", job["image"]);
        string script = ((List<string>)job["script"]).Single();
        Assert.Contains("-project=\"/workspace/Sample.uproject\"", script);
        Assert.Contains("-platform=Win64", script);
        Assert.Contains("-clientconfig=Shipping", script);
    }

    [Fact]
    public void Instantiate_FallsBackToDefaultsForMissingValues()
    {
        (string name, Dictionary<string, object> job) = JobTemplateCatalog.Instantiate("godot",
            new Dictionary<string, object>());

        Assert.Equal("godot-export", name);
        Assert.Equal("barichello/godot-ci:4.2.2", job["image"]);
    }

    [Fact]
    public void UpsertMap_WritesTemplateJobIntoYaml()
    {
        (string name, Dictionary<string, object> job) = JobTemplateCatalog.Instantiate("unity",
            new Dictionary<string, object> { ["targets"] = new List<string> { "WebGL", "Android" } });

        string yaml = JobConfigEditor.UpsertMap("", name, job);
        JobsSnapshot snapshot = JobConfigEditor.Read(yaml);

        JobSummary summary = snapshot.Jobs.Single();
        Assert.Equal("unity-build", summary.Name);
        Assert.Equal(3600, summary.Timeout);
        Assert.Equal("$UNITY_LICENSE", summary.Env["UNITY_LICENSE"]);
        Assert.Contains("matrix", summary.AdvancedKeys);
        Assert.Contains("cache", summary.AdvancedKeys);
    }
}
