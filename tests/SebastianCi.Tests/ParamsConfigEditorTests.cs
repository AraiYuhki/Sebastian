using SebastianCi.Web;

namespace SebastianCi.Tests;

public sealed class ParamsConfigEditorTests
{
    private const string BaseYaml = """
        # コメントは保持される
        name: sample
        image: alpine

        params:
          deployTarget:
            default: staging
            description: デプロイ先
            choices: [staging, production]

        jobs:
          build:
            script: [echo hi]
        """;

    [Fact]
    public void Read_ReturnsParamSummaries()
    {
        List<ParamSummary> parameters = ParamsConfigEditor.Read(BaseYaml);

        ParamSummary parameter = parameters.Single();
        Assert.Equal("deployTarget", parameter.Name);
        Assert.Equal("staging", parameter.Default);
        Assert.Equal("デプロイ先", parameter.Description);
        Assert.Equal(["staging", "production"], parameter.Choices);
    }

    [Fact]
    public void Read_RequiredParamHasNullDefault()
    {
        const string yaml = """
            params:
              apiKey:
                description: APIキー
            """;

        Assert.Null(ParamsConfigEditor.Read(yaml).Single().Default);
    }

    [Fact]
    public void Upsert_AddsParamAndKeepsComments()
    {
        ParamFormPayload payload = new(null, "releaseVersion", Required: true, null, "リリース版数", null);

        string result = ParamsConfigEditor.Upsert(BaseYaml, payload);

        Assert.Contains("# コメントは保持される", result);
        List<ParamSummary> parameters = ParamsConfigEditor.Read(result);
        Assert.Equal(2, parameters.Count);
        ParamSummary added = parameters.Single(parameter => parameter.Name == "releaseVersion");
        Assert.Null(added.Default);
        Assert.Equal("リリース版数", added.Description);
    }

    [Fact]
    public void Upsert_CreatesSectionWhenMissing()
    {
        ParamFormPayload payload = new(null, "target", Required: false, "staging", "", ["staging", "production"]);

        string result = ParamsConfigEditor.Upsert("image: alpine\njobs:\n  build:\n    script: [echo hi]", payload);

        ParamSummary parameter = ParamsConfigEditor.Read(result).Single();
        Assert.Equal("target", parameter.Name);
        Assert.Equal("staging", parameter.Default);
        Assert.Equal(["staging", "production"], parameter.Choices);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Upsert_ReplacesExistingParam()
    {
        ParamFormPayload payload = new("deployTarget", "deployTarget", Required: false, "production", "", null);

        string result = ParamsConfigEditor.Upsert(BaseYaml, payload);

        ParamSummary parameter = ParamsConfigEditor.Read(result).Single();
        Assert.Equal("production", parameter.Default);
        Assert.Empty(parameter.Choices);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Remove_DeletesParamAndSectionWhenLast()
    {
        string result = ParamsConfigEditor.Remove(BaseYaml, "deployTarget");

        Assert.Empty(ParamsConfigEditor.Read(result));
        Assert.DoesNotContain("params:", result);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Remove_KeepsSectionWhenOthersRemain()
    {
        string yaml = ParamsConfigEditor.Upsert(
            BaseYaml, new ParamFormPayload(null, "other", Required: false, "x", "", null));

        string result = ParamsConfigEditor.Remove(yaml, "other");

        Assert.Contains("params:", result);
        Assert.Equal("deployTarget", ParamsConfigEditor.Read(result).Single().Name);
    }

    [Fact]
    public void Remove_UnknownNameReturnsYamlUnchanged()
        => Assert.Equal(BaseYaml, ParamsConfigEditor.Remove(BaseYaml, "missing"));
}
