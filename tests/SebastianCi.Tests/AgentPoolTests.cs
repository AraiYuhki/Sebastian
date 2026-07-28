using SebastianCi.Core;
using SebastianCi.Models;

namespace SebastianCi.Tests;

public class AgentPoolTests
{
    [Fact]
    public void PickNext_PrefersLeastLoadedAgent()
    {
        AgentEndpoint a = new() { Url = "http://a" };
        AgentEndpoint b = new() { Url = "http://b" };
        AgentPool pool = new([a, b]);

        pool.Acquire(a);   // a は1件実行中、b は0件

        Assert.Equal(b, pool.PickNext([]));
    }

    [Fact]
    public void PickNext_SkipsAlreadyTriedAndReturnsNullWhenExhausted()
    {
        AgentEndpoint a = new() { Url = "http://a" };
        AgentPool pool = new([a]);

        Assert.Equal(a, pool.PickNext([]));
        Assert.Null(pool.PickNext([a]));
    }

    [Fact]
    public void Release_DecrementsLoadSoAgentBecomesPreferredAgain()
    {
        AgentEndpoint a = new() { Url = "http://a" };
        AgentEndpoint b = new() { Url = "http://b" };
        AgentPool pool = new([a, b]);

        pool.Acquire(a);
        pool.Release(a);   // 元に戻る → 定義順で a が選ばれる

        Assert.Equal(a, pool.PickNext([]));
    }

    [Fact]
    public void PickNext_FiltersByRequiredLabels()
    {
        AgentEndpoint linux = new() { Url = "http://linux", Labels = ["linux"] };
        AgentEndpoint mac = new() { Url = "http://mac", Labels = ["macos", "xcode"] };
        AgentPool pool = new([linux, mac]);

        pool.Acquire(mac);   // mac の方が混んでいても、ラベルを満たすのは mac だけ

        Assert.Equal(mac, pool.PickNext([], ["macos", "xcode"]));
        Assert.Equal(linux, pool.PickNext([], ["linux"]));
        Assert.Null(pool.PickNext([], ["windows"]));
    }

    [Fact]
    public void PickNext_EmptyLabelsMatchEveryAgent()
    {
        AgentEndpoint labeled = new() { Url = "http://a", Labels = ["gpu"] };
        AgentPool pool = new([labeled]);

        Assert.Equal(labeled, pool.PickNext([], []));
    }

    [Fact]
    public void Satisfies_RequiresAllLabels()
    {
        AgentEndpoint agent = new() { Url = "http://a", Labels = ["macos", "xcode"] };

        Assert.True(agent.Satisfies(["macos"]));
        Assert.True(agent.Satisfies(["macos", "xcode"]));
        Assert.False(agent.Satisfies(["macos", "gpu"]));
    }
}
