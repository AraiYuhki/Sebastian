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
}
