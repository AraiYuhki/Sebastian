using SebastianCi.Web;

namespace SebastianCi.Tests;

public sealed class AgentsConfigEditorTests
{
    private const string BaseYaml = """
        # コメントは保持される
        image: alpine

        agents:
          - url: http://agent-1:8771
            token: $AGENT_TOKEN
            labels: [linux, docker]

        jobs:
          build:
            script: [echo hi]
        """;

    [Fact]
    public void Read_ReturnsAgentSummaries()
    {
        List<AgentSummary> agents = AgentsConfigEditor.Read(BaseYaml);

        AgentSummary agent = agents.Single();
        Assert.Equal("http://agent-1:8771", agent.Url);
        Assert.Equal("$AGENT_TOKEN", agent.Token);
        Assert.Equal(["linux", "docker"], agent.Labels);
    }

    [Fact]
    public void Add_AppendsEntryToExistingSection()
    {
        string result = AgentsConfigEditor.Add(
            BaseYaml, "http://mac-agent:8771", "$AGENT_TOKEN", ["macos", "xcode"]);

        Assert.Contains("# コメントは保持される", result);
        List<AgentSummary> agents = AgentsConfigEditor.Read(result);
        Assert.Equal(2, agents.Count);
        AgentSummary added = agents.Single(agent => agent.Url == "http://mac-agent:8771");
        Assert.Equal(["macos", "xcode"], added.Labels);
    }

    [Fact]
    public void Add_CreatesSectionWhenMissing()
    {
        string result = AgentsConfigEditor.Add("image: alpine", "http://a:8771", "", []);

        AgentSummary agent = AgentsConfigEditor.Read(result).Single();
        Assert.Equal("http://a:8771", agent.Url);
        Assert.Equal("", agent.Token);
        Assert.Empty(agent.Labels);
    }

    [Fact]
    public void Remove_DeletesEntryAndSectionWhenLast()
    {
        string result = AgentsConfigEditor.Remove(BaseYaml, "http://agent-1:8771");

        Assert.Empty(AgentsConfigEditor.Read(result));
        Assert.DoesNotContain("agents:", result);
        Assert.Contains("jobs:", result);
    }

    [Fact]
    public void Remove_KeepsOtherEntries()
    {
        string yaml = AgentsConfigEditor.Add(BaseYaml, "http://agent-2:8771", "", ["gpu"]);

        string result = AgentsConfigEditor.Remove(yaml, "http://agent-1:8771");

        AgentSummary remaining = AgentsConfigEditor.Read(result).Single();
        Assert.Equal("http://agent-2:8771", remaining.Url);
        Assert.Contains("agents:", result);
    }

    [Fact]
    public void Remove_UnknownUrlReturnsYamlUnchanged()
        => Assert.Equal(BaseYaml, AgentsConfigEditor.Remove(BaseYaml, "http://missing:1"));
}
