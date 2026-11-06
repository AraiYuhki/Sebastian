using System.Diagnostics;
using SebastianCi.Web;

namespace SebastianCi.Tests;

/// <summary>ハブのワークスペース型ジョブ（ソース検証・取得・実行前の準備・登録）のテスト。</summary>
public sealed class WorkspaceJobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sebastian-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _savedProtocols = WorkspaceSync.AllowedProtocols;

    public WorkspaceJobTests()
    {
        Directory.CreateDirectory(_root);
        // 実際のネットワークを使わず、ローカルの git リポジトリを取得元にする。
        WorkspaceSync.AllowedProtocols = "https:ssh:file";
    }

    public void Dispose()
    {
        WorkspaceSync.AllowedProtocols = _savedProtocols;
        ForceDelete(_root);
    }

    private static void ForceDelete(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // .git のオブジェクトは読み取り専用
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo info = new("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false" }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} 失敗: {error}");
    }

    /// <summary>1コミットだけ入った Git リポジトリを作り、そのパスを返す。</summary>
    private string MakeSourceRepository(string name, string fileContent = "v1")
    {
        string path = Path.Combine(_root, "src-" + name);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(path, "a.txt"), fileContent);
        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "first");
        return path;
    }

    private static string ToUrl(string path) => new Uri(path).AbsoluteUri;

    // ---- ソースの検証 ----

    [Fact]
    public void NormalizeSources_FillsDirectoryFromUrl()
    {
        (List<SourceEntry>? sources, string? error) = HubStore.NormalizeSources(
        [
            new SourcePayload(" https://github.com/org/game.git ", " main ", null),
            new SourcePayload("git@github.com:org/game.git", null, ""),
            new SourcePayload("ssh://git@host/team/tools", "", "custom")
        ]);

        Assert.Null(error);
        Assert.Equal(new SourceEntry("https://github.com/org/game.git", "main", "game"), sources![0]);
        Assert.Equal(new SourceEntry("git@github.com:org/game.git", null, "game-2"), sources[1]);
        Assert.Equal(new SourceEntry("ssh://git@host/team/tools", null, "custom"), sources[2]);
    }

    [Fact]
    public void NormalizeSources_AllowsEmptyList()
    {
        (List<SourceEntry>? sources, string? error) = HubStore.NormalizeSources(null);
        Assert.Null(error);
        Assert.Empty(sources!);
    }

    [Theory]
    [InlineData("file:///etc/passwd", null, null)]
    [InlineData("https://user:secret@github.com/a/b.git", null, null)]
    [InlineData("-oProxyCommand=x", null, null)]
    [InlineData("https://github.com/a/b.git", "-x", null)]
    [InlineData("https://github.com/a/b.git", "a..b", null)]
    [InlineData("https://github.com/a/b.git", "x y", null)]
    [InlineData("https://github.com/a/b.git", null, "../escape")]
    [InlineData("https://github.com/a/b.git", null, "a/b")]
    [InlineData("https://github.com/a/b.git", null, ".hidden")]
    [InlineData("https://github.com/a/b.git", null, "a\\b")]
    public void NormalizeSources_RejectsUnsafeValues(string url, string? branch, string? directory)
        => Assert.NotNull(HubStore.NormalizeSources([new SourcePayload(url, branch, directory)]).Error);

    [Fact]
    public void NormalizeSources_RejectsDuplicateDirectoriesAndDotWithOthers()
    {
        Assert.NotNull(HubStore.NormalizeSources(
        [
            new SourcePayload("https://github.com/a/x.git", null, "same"),
            new SourcePayload("https://github.com/a/y.git", null, "SAME")
        ]).Error);
        Assert.NotNull(HubStore.NormalizeSources(
        [
            new SourcePayload("https://github.com/a/x.git", null, "."),
            new SourcePayload("https://github.com/a/y.git", null, "y")
        ]).Error);
        Assert.Null(HubStore.NormalizeSources([new SourcePayload("https://github.com/a/x.git", null, ".")]).Error);
    }

    [Fact]
    public void NormalizeSources_LimitsCount()
    {
        List<SourcePayload> many = Enumerable.Range(0, HubStore.MaxSources + 1)
            .Select(i => new SourcePayload($"https://github.com/a/r{i}.git", null, null)).ToList();
        Assert.NotNull(HubStore.NormalizeSources(many).Error);
    }

    // ---- ソースの取得 ----

    [Fact]
    public async Task PrepareAsync_ClonesThenUpdatesAndReturnsDistinctRevisions()
    {
        string source = MakeSourceRepository("one");
        string workspace = Path.Combine(_root, "ws");
        List<SourceEntry> sources = [new SourceEntry(ToUrl(source), "main", "one")];
        List<string> log = [];

        string? first = await WorkspaceSync.PrepareAsync(workspace, sources, log.Add, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Matches("^[0-9a-f]{40}$", first);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(workspace, "one", "a.txt")));
        Assert.DoesNotContain(log, line => line.StartsWith("Cloning into") || line.Contains("Cloning into"));

        File.WriteAllText(Path.Combine(source, "a.txt"), "v2");
        Git(source, "commit", "-q", "-am", "second");
        string? second = await WorkspaceSync.PrepareAsync(workspace, sources, log.Add, CancellationToken.None);

        Assert.NotNull(second);
        Assert.NotEqual(first, second); // 実行ごとに別のリビジョン（履歴に1回ずつ残す）
        Assert.Equal("v2", File.ReadAllText(Path.Combine(workspace, "one", "a.txt")));
    }

    [Fact]
    public async Task PrepareAsync_DiscardsLocalChangesInWorkspace()
    {
        string source = MakeSourceRepository("dirty");
        string workspace = Path.Combine(_root, "ws-dirty");
        List<SourceEntry> sources = [new SourceEntry(ToUrl(source), null, "dirty")];
        Assert.NotNull(await WorkspaceSync.PrepareAsync(workspace, sources, _ => { }, CancellationToken.None));

        File.WriteAllText(Path.Combine(workspace, "dirty", "a.txt"), "tampered");
        Assert.NotNull(await WorkspaceSync.PrepareAsync(workspace, sources, _ => { }, CancellationToken.None));

        Assert.Equal("v1", File.ReadAllText(Path.Combine(workspace, "dirty", "a.txt"))); // 毎回、取得元と同じ状態にそろう
    }

    [Fact]
    public async Task PrepareAsync_SupportsMultipleSourcesAndDotDirectory()
    {
        string a = MakeSourceRepository("a", "A");
        string b = MakeSourceRepository("b", "B");
        string multi = Path.Combine(_root, "ws-multi");
        Assert.NotNull(await WorkspaceSync.PrepareAsync(
            multi, [new SourceEntry(ToUrl(a), null, "a"), new SourceEntry(ToUrl(b), null, "b")], _ => { }, CancellationToken.None));
        Assert.Equal("A", File.ReadAllText(Path.Combine(multi, "a", "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(multi, "b", "a.txt")));

        string single = Path.Combine(_root, "ws-single");
        Assert.NotNull(await WorkspaceSync.PrepareAsync(
            single, [new SourceEntry(ToUrl(a), null, ".")], _ => { }, CancellationToken.None));
        Assert.Equal("A", File.ReadAllText(Path.Combine(single, "a.txt")));
    }

    [Fact]
    public async Task PrepareAsync_WithNoSources_StillReturnsRevision()
    {
        string? first = await WorkspaceSync.PrepareAsync(Path.Combine(_root, "empty"), [], _ => { }, CancellationToken.None);
        string? second = await WorkspaceSync.PrepareAsync(Path.Combine(_root, "empty"), [], _ => { }, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task PrepareAsync_ReportsFailureWithoutRevision()
    {
        List<string> log = [];
        string? revision = await WorkspaceSync.PrepareAsync(
            Path.Combine(_root, "ws-bad"), [new SourceEntry(ToUrl(Path.Combine(_root, "missing")), null, "x")],
            log.Add, CancellationToken.None);

        Assert.Null(revision);
        Assert.Contains(log, line => line.Contains("git が失敗しました"));
    }

    [Fact]
    public async Task PrepareAsync_RefusesNonEmptyNonRepositoryTarget()
    {
        string source = MakeSourceRepository("occupied");
        string workspace = Path.Combine(_root, "ws-occupied");
        Directory.CreateDirectory(Path.Combine(workspace, "occupied"));
        File.WriteAllText(Path.Combine(workspace, "occupied", "mine.txt"), "keep");

        List<string> log = [];
        Assert.Null(await WorkspaceSync.PrepareAsync(
            workspace, [new SourceEntry(ToUrl(source), null, "occupied")], log.Add, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(workspace, "occupied", "mine.txt"))); // 既存のファイルは消さない
    }

    // ---- 実行前の準備（RunManager） ----

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task RunManager_PrepareFailure_DoesNotStartAndReportsExitCode1()
    {
        RunManager manager = new(
            _root, "job.yaml", null,
            (log, _) => { log("準備に失敗"); return Task.FromResult<string?>(null); });

        Assert.True(manager.TryStart(false));
        await WaitUntilAsync(() => !manager.IsRunning);

        RunStatus status = manager.GetStatus();
        Assert.Equal(1, status.ExitCode);
        Assert.Contains("準備に失敗", status.OutputLines);
    }

    [Fact]
    public async Task RunManager_PrepareThrows_IsReportedAndReleasesTheLock()
    {
        RunManager manager = new(_root, "job.yaml", null, (_, _) => throw new InvalidOperationException("boom"));

        Assert.True(manager.TryStart(false));
        await WaitUntilAsync(() => !manager.IsRunning);

        Assert.Equal(1, manager.GetStatus().ExitCode);
        Assert.Contains(manager.GetStatus().OutputLines, line => line.Contains("boom"));
        Assert.True(manager.TryStart(false)); // 次の実行を受け付けられる
    }

    [Fact]
    public async Task RunManager_CountsPreparationAsRunning_SoSecondStartIsRefused()
    {
        TaskCompletionSource<string?> gate = new();
        RunManager manager = new(_root, "job.yaml", null, (_, _) => gate.Task);

        Assert.True(manager.TryStart(false));
        Assert.True(manager.IsRunning);
        Assert.False(manager.TryStart(false)); // 準備中も「実行中」。二重に始めない

        gate.SetResult(null);
        await WaitUntilAsync(() => !manager.IsRunning);
    }

    [Fact]
    public void RunManager_PassesRevisionToTheEngine()
    {
        RunManager manager = new(_root, "job.yaml", Path.Combine(_root, "data"));
        string revision = new('a', 40);

        string[] arguments = manager.BuildArguments("x.dll", false, null, null, false, revision).ToArray();

        int index = Array.IndexOf(arguments, "--revision");
        Assert.True(index >= 0);
        Assert.Equal(revision, arguments[index + 1]);
        Assert.DoesNotContain("--revision", manager.BuildArguments("x.dll", false, null, null, false));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef01", false)]
    [InlineData("abc", false)]
    [InlineData("../../etc/passwd", false)]
    public void CliOptions_RevisionMustBe40LowercaseHex(string value, bool valid)
    {
        SebastianCi.CliOptions? options = SebastianCi.CliOptions.Parse([".", "--revision", value]);
        Assert.Equal(valid, options is not null);
        if (valid) Assert.Equal(value, options!.Revision);
    }

    // ---- ジョブの登録（HubService） ----

    private (HubService Hub, string Data) CreateHub()
    {
        string data = Path.Combine(_root, "hub-" + Guid.NewGuid().ToString("N")[..8]);
        ServeOptions options = new(".", 0, data, ".sebastian-ci.yaml", Hub: true);
        return (new HubService(options, new AccessControl(options)), data);
    }

    [Fact]
    public void AddWorkspaceJob_CreatesLayoutAndPersists()
    {
        (HubService hub, string data) = CreateHub();

        HubResult result = hub.AddWorkspaceJob("全体ビルド", [new SourcePayload("https://github.com/o/r.git", "main", null)]);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("job", result.Id);
        string jobDirectory = Path.Combine(data, "jobs", "job");
        Assert.True(Directory.Exists(Path.Combine(jobDirectory, "workspace")));
        Assert.True(Directory.Exists(Path.Combine(jobDirectory, "data")));
        Assert.True(File.Exists(Path.Combine(jobDirectory, "job.yaml")));

        RepositoryEntry saved = new HubStore(data).Load().Repositories.Single();
        Assert.True(saved.IsWorkspaceJob);
        Assert.Equal(new SourceEntry("https://github.com/o/r.git", "main", "r"), saved.Sources!.Single());
        Assert.Equal(RepositoryState.Ready, hub.Find("job")!.State);
        Assert.NotNull(hub.Resolve("job"));
    }

    [Fact]
    public void AddWorkspaceJob_RejectsBadInputWithoutCreatingAnything()
    {
        (HubService hub, string data) = CreateHub();

        Assert.False(hub.AddWorkspaceJob("", []).Ok);
        Assert.False(hub.AddWorkspaceJob("x", [new SourcePayload("file:///etc", null, null)]).Ok);
        Assert.Empty(hub.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(data, "jobs")));
    }

    [Fact]
    public void AddWorkspaceJob_DoesNotReuseDirectoryOfRemovedJob()
    {
        (HubService hub, string data) = CreateHub();
        string first = hub.AddWorkspaceJob("build", []).Id!;
        File.WriteAllText(Path.Combine(data, "jobs", first, "job.yaml"), "name: old");
        Assert.True(hub.Remove(first).Ok);

        string second = hub.AddWorkspaceJob("build", []).Id!;

        Assert.NotEqual(first, second); // 外したジョブの定義・履歴を、同名の新しいジョブが引き継がない
        Assert.Equal("name: old", File.ReadAllText(Path.Combine(data, "jobs", first, "job.yaml")));
    }

    [Fact]
    public void Update_WorkspaceJob_ChangesNameAndSourcesButNotPaths()
    {
        (HubService hub, string data) = CreateHub();
        string id = hub.AddWorkspaceJob("build", [new SourcePayload("https://github.com/o/a.git", null, "a")]).Id!;
        string workspace = hub.Find(id)!.Entry.Path;

        HubResult result = hub.Update(id, "renamed", "C:\\elsewhere", "other.yaml",
            [new SourcePayload("https://github.com/o/b.git", "dev", "b")]);

        Assert.True(result.Ok, result.Message);
        RepositoryEntry entry = hub.Find(id)!.Entry;
        Assert.Equal("renamed", entry.Name);
        Assert.Equal(workspace, entry.Path); // 作業場と定義ファイルの場所は変えられない
        Assert.EndsWith("job.yaml", entry.ConfigFileName);
        Assert.Equal(new SourceEntry("https://github.com/o/b.git", "dev", "b"), entry.Sources!.Single());
        Assert.Equal("dev", new HubStore(data).Load().Repositories.Single().Sources!.Single().Branch);
    }

    [Fact]
    public void Update_WorkspaceJob_RejectsInvalidSourcesAndKeepsOldOnes()
    {
        (HubService hub, _) = CreateHub();
        string id = hub.AddWorkspaceJob("build", [new SourcePayload("https://github.com/o/a.git", null, "a")]).Id!;

        Assert.False(hub.Update(id, null, null, null, [new SourcePayload("ftp://x/y", null, null)]).Ok);

        Assert.Equal("a", hub.Find(id)!.Entry.Sources!.Single().Directory);
    }

    [Fact]
    public void LegacyRepositoryEntries_AreNotWorkspaceJobs()
    {
        string data = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "hub.json"),
            """{"repositories":[{"id":"old","name":"old","path":"C:\\x","configFileName":".sebastian-ci.yaml"}]}""");

        RepositoryEntry entry = new HubStore(data).Load().Repositories.Single();

        Assert.False(entry.IsWorkspaceJob);
        Assert.Null(entry.Sources);
    }
}
