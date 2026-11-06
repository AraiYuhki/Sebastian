using SebastianCi.Web;

namespace SebastianCi.Tests;

public class HubTests
{
    // ---- 入力検証 ----

    [Theory]
    [InlineData("My Game", "my-game")]
    [InlineData("  Alpha_Beta  ", "alpha-beta")]
    [InlineData("ゲーム", "repo")]
    [InlineData("---", "repo")]
    [InlineData("A  --  B", "a-b")]
    public void CreateId_MakesUrlSafeSlug(string name, string expected)
        => Assert.Equal(expected, HubStore.CreateId(name, []));

    [Fact]
    public void CreateId_AvoidsCollisions()
    {
        Assert.Equal("repo-2", HubStore.CreateId("ゲーム", ["repo"]));
        Assert.Equal("game-3", HubStore.CreateId("game", ["game", "game-2"]));
    }

    [Fact]
    public void CreateId_ProducesValidIds()
    {
        foreach (string name in new[] { "My Game", "ゲーム", new string('x', 100), "a/b", "../../etc" })
        {
            Assert.True(HubStore.IsValidId(HubStore.CreateId(name, [])), name);
        }
    }

    [Theory]
    [InlineData("alpha", true)]
    [InlineData("a-1", true)]
    [InlineData("-a", false)]
    [InlineData("A", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidId(string? id, bool expected) => Assert.Equal(expected, HubStore.IsValidId(id));

    [Theory]
    [InlineData(".sebastian-ci.yaml", true)]
    [InlineData("ci.yml", true)]
    [InlineData("../ci.yaml", false)]
    [InlineData("sub/ci.yaml", false)]
    [InlineData("sub\\ci.yaml", false)]
    [InlineData("ci.txt", false)]
    [InlineData("", false)]
    public void ValidateConfigFileName(string name, bool valid)
        => Assert.Equal(valid, HubStore.ValidateConfigFileName(name) is null);

    [Theory]
    [InlineData("game", true)]
    [InlineData("ゲーム", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("bad\u0007name", false)]
    public void ValidateName(string name, bool valid) => Assert.Equal(valid, HubStore.ValidateName(name) is null);

    [Fact]
    public void ValidateName_RejectsTooLong() => Assert.NotNull(HubStore.ValidateName(new string('a', 61)));

    [Theory]
    [InlineData("https://github.com/org/repo.git", true)]
    [InlineData("ssh://git@github.com/org/repo.git", true)]
    [InlineData("git@github.com:org/repo.git", true)]
    [InlineData("https://user@github.com/org/repo.git", true)]
    [InlineData("https://user:token@github.com/org/repo.git", false)]
    [InlineData("http://github.com/org/repo.git", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/local/path", false)]
    [InlineData("ext::sh -c evil", false)]
    [InlineData("--upload-pack=evil", false)]
    [InlineData("-oProxyCommand=evil", false)]
    [InlineData("https://github.com/a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void GitCloner_ValidateUrl(string? url, bool valid)
        => Assert.Equal(valid, GitCloner.ValidateUrl(url) is null);

    [Fact]
    public void GitCloner_MasksCredentialsInOutput()
        => Assert.Equal(
            "fatal: unable to access 'https://***@github.com/o/r.git/'",
            GitCloner.MaskCredentials("fatal: unable to access 'https://user:tok@github.com/o/r.git/'"));

    // ---- 保存 ----

    [Fact]
    public void HubStore_RoundTrips()
    {
        using TempDirectory temp = new();
        HubStore store = new(temp.Path);
        Assert.Empty(store.Load().Repositories);

        store.Save(new HubData
        {
            Repositories = [new RepositoryEntry("a", "A", "/x", ".sebastian-ci.yaml", "https://example.com/a.git")]
        });

        RepositoryEntry loaded = Assert.Single(new HubStore(temp.Path).Load().Repositories);
        Assert.Equal("https://example.com/a.git", loaded.CloneUrl);
    }

    [Fact]
    public void ValidateRepositoryPath_RequiresGitDirectory()
    {
        using TempDirectory temp = new();
        Assert.NotNull(HubStore.ValidateRepositoryPath(Path.Combine(temp.Path, "missing")));
        Assert.NotNull(HubStore.ValidateRepositoryPath(temp.Path));

        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        Assert.Null(HubStore.ValidateRepositoryPath(temp.Path));
    }

    // ---- 登録・編集・削除 ----

    private static (HubService Hub, TempDirectory Data, TempDirectory Repos) CreateHub()
    {
        TempDirectory data = new();
        TempDirectory repos = new();
        ServeOptions options = new(".", 0, data.Path, ".sebastian-ci.yaml", Hub: true);
        return (new HubService(options, new AccessControl(options)), data, repos);
    }

    private static string MakeRepository(TempDirectory repos, string name)
    {
        string path = Path.Combine(repos.Path, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    [Fact]
    public void AddLocal_RegistersAndPersists()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            string path = MakeRepository(repos, "game");

            HubResult result = hub.AddLocal("My Game", path, null);

            Assert.True(result.Ok, result.Message);
            Assert.Equal("my-game", result.Id);
            Assert.Equal(RepositoryState.Ready, hub.Find("my-game")!.State);
            Assert.NotNull(hub.Resolve("my-game"));
            Assert.Equal("my-game", Assert.Single(new HubStore(data.Path).Load().Repositories).Id);
            hub.Remove("my-game");
        }
    }

    [Fact]
    public void AddLocal_RejectsDuplicatePathAndInvalidInput()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            string path = MakeRepository(repos, "game");
            Assert.True(hub.AddLocal("a", path, null).Ok);

            Assert.False(hub.AddLocal("b", path, null).Ok);
            Assert.False(hub.AddLocal("", MakeRepository(repos, "other"), null).Ok);
            Assert.False(hub.AddLocal("c", Path.Combine(repos.Path, "nope"), null).Ok);
            Assert.False(hub.AddLocal("d", MakeRepository(repos, "third"), "../evil.yaml").Ok);
            Assert.Single(hub.Snapshot());
            hub.Remove("a");
        }
    }

    [Fact]
    public void AddByClone_RejectsBadUrlWithoutRegistering()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            Assert.False(hub.AddByClone("x", "file:///etc", null).Ok);
            Assert.False(hub.AddByClone("x", "https://u:p@github.com/o/r.git", null).Ok);
            Assert.Empty(hub.Snapshot());
        }
    }

    [Fact]
    public void Update_RenamesAndChangesConfigFile()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            hub.AddLocal("old", MakeRepository(repos, "game"), null);

            Assert.True(hub.Update("old", "New Name", null, "ci.yaml").Ok);

            RepositoryView view = hub.Find("old")!;
            Assert.Equal("New Name", view.Entry.Name);
            Assert.Equal("ci.yaml", view.Entry.ConfigFileName);
            Assert.Equal("ci.yaml", view.Api!.ConfigFileName);
            Assert.Equal("ci.yaml", new HubStore(data.Path).Load().Repositories[0].ConfigFileName);
            hub.Remove("old");
        }
    }

    [Fact]
    public void Update_RejectsUnknownIdAndPathCollision()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            string first = MakeRepository(repos, "one");
            hub.AddLocal("one", first, null);
            hub.AddLocal("two", MakeRepository(repos, "two"), null);

            Assert.False(hub.Update("missing", "x", null, null).Ok);
            Assert.False(hub.Update("two", null, first, null).Ok);
            Assert.False(hub.Update("two", "", null, null).Ok);
            Assert.False(hub.Update("two", null, null, "../x.yaml").Ok);
            hub.Remove("one");
            hub.Remove("two");
        }
    }

    [Fact]
    public void Remove_UnregistersWithoutDeletingFiles()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            string path = MakeRepository(repos, "game");
            hub.AddLocal("game", path, null);

            Assert.True(hub.Remove("game").Ok);

            Assert.Null(hub.Find("game"));
            Assert.Null(hub.Resolve("game"));
            Assert.True(Directory.Exists(path));
            Assert.Empty(new HubStore(data.Path).Load().Repositories);
            Assert.False(hub.Remove("game").Ok);
        }
    }

    [Fact]
    public async Task Summary_ReportsJobsSchedulesAndBrokenConfig()
    {
        (HubService hub, TempDirectory data, TempDirectory repos) = CreateHub();
        using (data) using (repos)
        {
            string path = MakeRepository(repos, "game");
            File.WriteAllText(Path.Combine(path, ".sebastian-ci.yaml"), """
                name: x
                image: alpine
                jobs:
                  build:
                    script: [echo hi]
                  test:
                    needs: [build]
                    script: [echo t]
                schedules:
                  nightly:
                    cron: "0 3 * * *"
                    job: build
                """);
            hub.AddLocal("game", path, null);

            RepositorySummary summary = await hub.Resolve("game")!.GetSummaryAsync(5, CancellationToken.None);

            Assert.True(summary.ConfigExists);
            Assert.Null(summary.ConfigError);
            Assert.Equal(["build", "test"], summary.Jobs.Select(job => job.Name));
            Assert.Equal(1, summary.ScheduleCount);
            Assert.NotNull(summary.NextRunAt);
            Assert.Null(summary.LastRun);

            File.WriteAllText(Path.Combine(path, ".sebastian-ci.yaml"), "jobs: [unclosed");
            RepositorySummary broken = await hub.Resolve("game")!.GetSummaryAsync(5, CancellationToken.None);
            Assert.NotNull(broken.ConfigError);
            hub.Remove("game");
        }
    }

    // ---- 権限ポリシー（/r/{id} 配下） ----

    [Theory]
    [InlineData("/r/alpha/api/jobs", "/api/jobs")]
    [InlineData("/r/alpha/", "/")]
    [InlineData("/r/alpha", "/")]
    [InlineData("/r/a-1/api/run/ws", "/api/run/ws")]
    [InlineData("/api/jobs", "/api/jobs")]
    [InlineData("/hub/api/overview", "/hub/api/overview")]
    [InlineData("/r/Alpha/api/jobs", "/r/Alpha/api/jobs")]
    [InlineData("/rx/api/jobs", "/rx/api/jobs")]
    public void StripRepositoryPrefix(string path, string expected)
        => Assert.Equal(expected, AccessPolicy.StripRepositoryPrefix(path));

    [Theory]
    [InlineData("GET", "/hub/api/overview", RequiredAccess.User)]
    [InlineData("GET", "/r/alpha/", RequiredAccess.User)]
    [InlineData("GET", "/r/alpha/api/jobs", RequiredAccess.User)]
    [InlineData("GET", "/r/alpha/api/history/abc", RequiredAccess.User)]
    [InlineData("GET", "/r/alpha/api/logs/abc/build", RequiredAccess.User)]
    [InlineData("POST", "/r/alpha/api/run", RequiredAccess.User)]
    [InlineData("GET", "/r/alpha/api/run/ws", RequiredAccess.User)]
    [InlineData("GET", "/r/alpha/api/config", RequiredAccess.Admin)]
    [InlineData("GET", "/r/alpha/api/agents", RequiredAccess.Admin)]
    [InlineData("GET", "/r/alpha/api/plugins", RequiredAccess.Admin)]
    [InlineData("POST", "/r/alpha/api/config", RequiredAccess.Admin)]
    [InlineData("POST", "/r/alpha/api/jobs", RequiredAccess.Admin)]
    [InlineData("POST", "/r/alpha/api/schedules/remove", RequiredAccess.Admin)]
    [InlineData("POST", "/hub/api/repos", RequiredAccess.Admin)]
    [InlineData("POST", "/hub/api/repos/update", RequiredAccess.Admin)]
    [InlineData("POST", "/hub/api/repos/remove", RequiredAccess.Admin)]
    [InlineData("GET", "/hub/api/repos", RequiredAccess.Admin)]
    [InlineData("GET", "/r/Alpha/api/jobs", RequiredAccess.Admin)]
    [InlineData("GET", "/r/alpha/login", RequiredAccess.Admin)]
    [InlineData("POST", "/r/alpha/logout", RequiredAccess.Admin)]
    [InlineData("POST", "/r/alpha/hub/api/repos", RequiredAccess.Admin)]
    public void Policy_AppliesToHubPaths(string method, string path, RequiredAccess expected)
        => Assert.Equal(expected, AccessPolicy.Required(method, path));

    [Theory]
    [InlineData("/api/jobs", true)]
    [InlineData("/r/alpha/api/jobs", true)]
    [InlineData("/hub/api/overview", true)]
    [InlineData("/", false)]
    [InlineData("/r/alpha/", false)]
    public void IsApiPath(string path, bool expected) => Assert.Equal(expected, AccessPolicy.IsApiPath(path));

    // ---- ページ ----

    [Fact]
    public void RepositoryPage_RewritesApiTargetsAndEscapesName()
    {
        string html = RepositoryPage.Render("alpha", "<b>Alpha & \"co\"</b>");

        Assert.Contains("var B=\"/r/alpha\"", html);
        Assert.Contains("\"/r/alpha/api/run/ws\"", html);
        Assert.DoesNotContain("location.host + \"/api/run/ws\"", html);
        Assert.Contains("&lt;b&gt;Alpha &amp; &quot;co&quot;&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Alpha", html);
        Assert.Contains("href=\"/\"", html);
    }

    [Theory]
    [InlineData("Alpha")]
    [InlineData("a/b")]
    [InlineData("\"><script>")]
    [InlineData("")]
    public void RepositoryPage_RejectsUnsafeId(string id)
        => Assert.Throws<ArgumentException>(() => RepositoryPage.Render(id, "x"));

    // ---- オプション ----

    [Fact]
    public void ServeOptions_ParsesHubAndUsesHomeDirectoryByDefault()
    {
        ServeOptions options = ServeOptions.Parse(["--hub", "--port", "9000"])!;

        Assert.True(options.Hub);
        Assert.Equal(9000, options.Port);
        Assert.Equal(ServeOptions.DefaultHubDirectory, options.ResolveDataRootPath());
        Assert.Equal(["--hub", "--port", "9000", "--config", options.ConfigFileName], options.ToServeArguments());
    }

    [Fact]
    public void ServeOptions_HubHonorsDataDirectory()
    {
        ServeOptions options = ServeOptions.Parse(["--hub", "--data-dir", "hubdata"])!;

        Assert.Equal(Path.GetFullPath("hubdata"), options.ResolveDataRootPath());
        Assert.Contains("--data-dir", options.ToServeArguments());
    }

    [Fact]
    public void ServeOptions_SingleModeIsUnchanged()
    {
        ServeOptions options = ServeOptions.Parse(["/repo"])!;

        Assert.False(options.Hub);
        Assert.EndsWith(".sebastian-ci", options.ResolveDataRootPath());
        Assert.Equal(Path.GetFullPath("/repo"), options.ToServeArguments()[0]);
    }

    [Fact]
    public void AuthOptions_ParsesHub()
        => Assert.True(AuthOptions.Parse(["list", "--hub"])!.Hub);

    [Fact]
    public void DaemonOptions_AcceptsHub()
        => Assert.True(DaemonOptions.Parse(["start", "--hub"])!.Serve.Hub);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sebastian-hub-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
