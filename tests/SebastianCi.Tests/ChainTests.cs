using SebastianCi.Web;

namespace SebastianCi.Tests;

/// <summary>ハブのジョブ間の連鎖（検証・循環の拒否・登録・削除の連動・完了の通知）のテスト。</summary>
public sealed class ChainTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sebastian-chain-" + Guid.NewGuid().ToString("N"));

    public ChainTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private (HubService Hub, string Data) CreateHub(params string[] jobNames)
    {
        string data = Path.Combine(_root, "hub-" + Guid.NewGuid().ToString("N")[..8]);
        ServeOptions options = new(".", 0, data, ".sebastian-ci.yaml", Hub: true);
        HubService hub = new(options, new AccessControl(options));
        foreach (string name in jobNames) Assert.True(hub.AddWorkspaceJob(name, []).Ok);
        return (hub, data);
    }

    private static ChainPayload Payload(string from, string to, string on = "success", string? job = null, bool? enabled = null)
        => new(from, to, on, job, null, null, null, enabled);

    // ---- 形式の検証 ----

    [Theory]
    [InlineData("success", null, true)]
    [InlineData("failure", "deploy", true)]
    [InlineData("always", "a.b-c_1", true)]
    [InlineData("sometimes", null, false)]
    [InlineData("success", "bad name", false)]
    [InlineData("success", "../x", false)]
    public void ValidateChainFields_ChecksConditionAndJobName(string on, string? job, bool valid)
        => Assert.Equal(valid, HubStore.ValidateChainFields(on, job, null) is null);

    [Fact]
    public void ValidateChainFields_ChecksParameters()
    {
        Assert.Null(HubStore.ValidateChainFields("success", null, new() { ["ENV"] = "staging" }));
        Assert.NotNull(HubStore.ValidateChainFields("success", null, new() { ["bad-name"] = "x" }));
        Assert.NotNull(HubStore.ValidateChainFields("success", null, new() { ["A"] = "line1\nline2" }));
        Assert.NotNull(HubStore.ValidateChainFields("success", null, new() { ["A"] = new string('x', 1001) }));
        Assert.NotNull(HubStore.ValidateChainFields(
            "success", null, Enumerable.Range(0, HubStore.MaxChainParams + 1).ToDictionary(i => $"P{i}", _ => "v")));
    }

    // ---- 循環 ----

    [Fact]
    public void WouldCreateCycle_DetectsDirectAndIndirectLoops()
    {
        List<ChainEntry> chains = [new("c1", "a", "b", "success"), new("c2", "b", "c", "success")];

        Assert.True(HubStore.WouldCreateCycle(chains, "b", "a"));
        Assert.True(HubStore.WouldCreateCycle(chains, "c", "a"));
        Assert.False(HubStore.WouldCreateCycle(chains, "a", "c")); // 合流・近道は循環ではない
        Assert.False(HubStore.WouldCreateCycle(chains, "c", "d"));
    }

    [Fact]
    public void WouldCreateCycle_IgnoresTheChainBeingEdited()
    {
        List<ChainEntry> chains = [new("c1", "a", "b", "success")];

        Assert.True(HubStore.WouldCreateCycle(chains, "b", "a"));
        Assert.False(HubStore.WouldCreateCycle(chains, "a", "b", ignoreId: "c1"));
    }

    // ---- 登録・編集・削除 ----

    [Fact]
    public void AddChain_RegistersAndPersists()
    {
        (HubService hub, string data) = CreateHub("alpha", "beta");

        HubResult result = hub.AddChain(Payload("alpha", "beta", "failure", "hello"));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("c1", result.Id);
        ChainEntry saved = new HubStore(data).Load().Chains.Single();
        Assert.Equal(new ChainEntry("c1", "alpha", "beta", "failure", "hello"), saved with { Params = null });
        Assert.True(saved.Rebuild); // 連鎖で起動した実行は、既定で実行済みのコミットでも再実行する
        Assert.False(saved.Yes);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public void AddChain_RejectsBadInput()
    {
        (HubService hub, _) = CreateHub("alpha", "beta");

        Assert.False(hub.AddChain(Payload("alpha", "alpha")).Ok);             // 自分自身
        Assert.False(hub.AddChain(Payload("alpha", "missing")).Ok);           // 起動先がない
        Assert.False(hub.AddChain(Payload("missing", "beta")).Ok);            // 起動元がない
        Assert.False(hub.AddChain(Payload("alpha", "beta", "sometimes")).Ok); // 条件が不正
        Assert.False(hub.AddChain(Payload("alpha", "beta", job: "nope")).Ok); // 起動先にないジョブ
        Assert.Empty(hub.ChainSnapshot());
    }

    [Fact]
    public void AddChain_RejectsLoops()
    {
        (HubService hub, _) = CreateHub("a", "b", "c");
        Assert.True(hub.AddChain(Payload("a", "b")).Ok);
        Assert.True(hub.AddChain(Payload("b", "c")).Ok);

        HubResult direct = hub.AddChain(Payload("b", "a"));
        HubResult indirect = hub.AddChain(Payload("c", "a"));

        Assert.False(direct.Ok);
        Assert.False(indirect.Ok);
        Assert.Equal(2, hub.ChainSnapshot().Count);
    }

    [Fact]
    public void UpdateChain_KeepsUnspecifiedFieldsAndChecksLoops()
    {
        (HubService hub, _) = CreateHub("a", "b", "c");
        string id = hub.AddChain(new ChainPayload("a", "b", "success", "hello", new() { ["ENV"] = "x" }, false, true, true)).Id!;
        Assert.True(hub.AddChain(Payload("b", "c")).Ok);

        Assert.True(hub.UpdateChain(id, new ChainPayload(null, null, null, null, null, null, null, false)).Ok);

        ChainEntry updated = hub.ChainSnapshot().Single(item => item.Chain.Id == id).Chain;
        Assert.False(updated.Enabled);
        Assert.Equal("hello", updated.Job);
        Assert.Equal("x", updated.Params!["ENV"]);
        Assert.False(updated.Rebuild);
        Assert.True(updated.Yes);

        // a→b を a→c に付け替えるのは良いが、c→a にして b→c→a→… と回るのは拒否する
        Assert.True(hub.UpdateChain(id, Payload("a", "c")).Ok);
        Assert.False(hub.UpdateChain(id, Payload("c", "b")).Ok);
        Assert.False(hub.UpdateChain("missing", Payload("a", "b")).Ok);
    }

    [Fact]
    public void RemovingAnEntry_RemovesItsChains()
    {
        (HubService hub, string data) = CreateHub("a", "b", "c");
        hub.AddChain(Payload("a", "b"));
        hub.AddChain(Payload("b", "c"));
        hub.AddChain(Payload("a", "c"));

        HubResult result = hub.Remove("b");

        Assert.True(result.Ok);
        Assert.Contains("2 件", result.Message);
        ChainEntry remaining = hub.ChainSnapshot().Single().Chain;
        Assert.Equal(("a", "c"), (remaining.From, remaining.To));
        Assert.Single(new HubStore(data).Load().Chains);
    }

    [Fact]
    public void RemoveChain_DeletesAndIdsKeepIncreasing()
    {
        (HubService hub, _) = CreateHub("a", "b");
        string first = hub.AddChain(Payload("a", "b")).Id!;
        Assert.True(hub.RemoveChain(first).Ok);
        Assert.False(hub.RemoveChain(first).Ok);

        Assert.Equal("c1", hub.AddChain(Payload("a", "b")).Id);
        Assert.Equal("c2", hub.AddChain(Payload("a", "b")).Id);
    }

    [Fact]
    public void LimitsTheNumberOfChains()
    {
        (HubService hub, _) = CreateHub("a", "b");
        for (int i = 0; i < HubStore.MaxChains; i++) Assert.True(hub.AddChain(Payload("a", "b")).Ok);

        Assert.False(hub.AddChain(Payload("a", "b")).Ok);
    }

    [Fact]
    public void LegacyHubFile_WithoutChains_LoadsEmpty()
    {
        string data = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "hub.json"), """{"repositories":[]}""");

        Assert.Empty(new HubStore(data).Load().Chains);
    }

    // ---- 実行の完了通知（RunManager） ----

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task RunManager_ReportsCompletionWithChainPath_WhenPreparationFails()
    {
        RunManager manager = new(_root, "job.yaml", null, (_, _) => Task.FromResult<string?>(null));
        RunCompletion? completion = null;
        manager.Completed += c => completion = c;

        Assert.True(manager.TryStart(false, chainPath: ["a", "b"], startNote: "🔗 連鎖テスト"));
        await WaitUntilAsync(() => completion is not null);

        Assert.Equal(1, completion!.ExitCode);
        Assert.False(completion.IsSuccess);
        Assert.False(completion.Skipped);
        Assert.Equal(["a", "b"], completion.ChainPath);
        Assert.Equal("🔗 連鎖テスト", manager.GetStatus().OutputLines[0]); // 連鎖で始まったことが実行ログの先頭に出る
    }

    [Fact]
    public async Task RunManager_CompletionHandlerCanStartTheNextRun()
    {
        int runs = 0;
        RunManager manager = new(_root, "job.yaml", null, (_, _) => { Interlocked.Increment(ref runs); return Task.FromResult<string?>(null); });
        manager.Completed += _ =>
        {
            // 完了通知はロックの外で呼ばれるので、中で次の実行を始められる（連鎖の起動先が同じ実行管理でも詰まらない）。
            if (runs < 2) Assert.True(manager.TryStart(false));
        };

        Assert.True(manager.TryStart(false));
        await WaitUntilAsync(() => runs == 2 && !manager.IsRunning);
    }

    [Fact]
    public async Task RunManager_FailingCompletionHandler_DoesNotBreakTheManager()
    {
        RunManager manager = new(_root, "job.yaml", null, (_, _) => Task.FromResult<string?>(null));
        manager.Completed += _ => throw new InvalidOperationException("handler failed");

        Assert.True(manager.TryStart(false));
        await WaitUntilAsync(() => !manager.IsRunning);

        Assert.True(manager.TryStart(false)); // 通知先の失敗があっても、次の実行は受け付ける
    }
}
