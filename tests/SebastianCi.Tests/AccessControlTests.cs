using SebastianCi.Web;

namespace SebastianCi.Tests;

public class AccessControlTests
{
    // ---- パスワード ----

    [Fact]
    public void PasswordHasher_VerifiesCorrectAndRejectsWrong()
    {
        string hash = PasswordHasher.Hash("correct-horse-battery");

        Assert.True(PasswordHasher.Verify("correct-horse-battery", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct-horse-battery"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("pbkdf2-sha256$abc$x$y")]
    [InlineData("pbkdf2-sha256$1$!!$!!")]
    public void PasswordHasher_RejectsMalformedStoredValue(string stored)
        => Assert.False(PasswordHasher.Verify("anything", stored));

    // ---- ロール判定 ----

    private static UserData SampleData() => new()
    {
        LocalUsers =
        [
            new LocalUser("alice", PasswordHasher.Hash("alice-password-1"), AccessRole.Admin),
            new LocalUser("bob", PasswordHasher.Hash("bob-password-12"), AccessRole.User)
        ],
        GoogleAdmins = ["boss@example.co.jp", "outside@gmail.com"],
        GoogleDomains = ["example.co.jp"]
    };

    [Fact]
    public void Authenticate_ReturnsRoleOnlyForCorrectPassword()
    {
        UserData data = SampleData();

        Assert.Equal(AccessRole.Admin, data.Authenticate("ALICE", "alice-password-1"));
        Assert.Equal(AccessRole.User, data.Authenticate("bob", "bob-password-12"));
        Assert.Null(data.Authenticate("bob", "alice-password-1"));
        Assert.Null(data.Authenticate("nobody", "alice-password-1"));
    }

    [Theory]
    [InlineData("boss@example.co.jp", true, AccessRole.Admin)]
    [InlineData("Boss@Example.co.jp", true, AccessRole.Admin)]
    [InlineData("outside@gmail.com", true, AccessRole.Admin)]
    [InlineData("staff@example.co.jp", true, AccessRole.User)]
    [InlineData("stranger@gmail.com", true, null)]
    [InlineData("staff@sub.example.co.jp", true, null)]
    [InlineData("staff@evil-example.co.jp", true, null)]
    [InlineData("boss@example.co.jp", false, null)]
    [InlineData("staff@example.co.jp", false, null)]
    [InlineData(null, true, null)]
    [InlineData("not-an-email", true, null)]
    public void ResolveGoogle_AppliesAdminListThenDomain(string? email, bool verified, AccessRole? expected)
        => Assert.Equal(expected, SampleData().ResolveGoogle(email, verified));

    [Fact]
    public void ResolveSession_ReflectsRemovedAndChangedAccounts()
    {
        UserData data = SampleData();
        Assert.Equal(AccessRole.User, data.ResolveSession(AccessControl.LocalKind, "bob"));

        data.LocalUsers.RemoveAll(user => user.Username == "bob");
        Assert.Null(data.ResolveSession(AccessControl.LocalKind, "bob"));

        data.GoogleDomains.Clear();
        Assert.Null(data.ResolveSession(AccessControl.GoogleKind, "staff@example.co.jp"));
        Assert.Equal(AccessRole.Admin, data.ResolveSession(AccessControl.GoogleKind, "boss@example.co.jp"));
    }

    // ---- 権限ポリシー ----

    [Theory]
    [InlineData("GET", "/login", RequiredAccess.Anonymous)]
    [InlineData("POST", "/login", RequiredAccess.Anonymous)]
    [InlineData("GET", "/signin-google", RequiredAccess.Anonymous)]
    [InlineData("GET", "/", RequiredAccess.User)]
    [InlineData("GET", "/api/jobs", RequiredAccess.User)]
    [InlineData("GET", "/api/history/abc123", RequiredAccess.User)]
    [InlineData("GET", "/api/logs/abc/build", RequiredAccess.User)]
    [InlineData("GET", "/api/run/ws", RequiredAccess.User)]
    [InlineData("POST", "/api/run", RequiredAccess.User)]
    [InlineData("GET", "/api/config", RequiredAccess.Admin)]
    [InlineData("GET", "/api/agents", RequiredAccess.Admin)]
    [InlineData("GET", "/api/plugins", RequiredAccess.Admin)]
    [InlineData("POST", "/api/config", RequiredAccess.Admin)]
    [InlineData("POST", "/api/jobs", RequiredAccess.Admin)]
    [InlineData("POST", "/api/jobs/remove", RequiredAccess.Admin)]
    [InlineData("POST", "/api/schedules", RequiredAccess.Admin)]
    [InlineData("DELETE", "/api/jobs", RequiredAccess.Admin)]
    [InlineData("GET", "/api/jobs/", RequiredAccess.Admin)]
    [InlineData("GET", "/api/unknown-new-endpoint", RequiredAccess.Admin)]
    public void Policy_RequiredAccess(string method, string path, RequiredAccess expected)
        => Assert.Equal(expected, AccessPolicy.Required(method, path));

    [Fact]
    public void Policy_Satisfies()
    {
        Assert.True(AccessPolicy.Satisfies(AccessRole.Admin, RequiredAccess.Admin));
        Assert.True(AccessPolicy.Satisfies(AccessRole.Admin, RequiredAccess.User));
        Assert.True(AccessPolicy.Satisfies(AccessRole.User, RequiredAccess.User));
        Assert.False(AccessPolicy.Satisfies(AccessRole.User, RequiredAccess.Admin));
        Assert.False(AccessPolicy.Satisfies(null, RequiredAccess.User));
        Assert.True(AccessPolicy.Satisfies(null, RequiredAccess.Anonymous));
    }

    // ---- ログイン試行の制限 ----

    [Fact]
    public void Throttle_BlocksAfterLimitAndRecoversAfterWindow()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        LoginThrottle throttle = new(3, TimeSpan.FromMinutes(10), () => now);

        for (int i = 0; i < 3; i++) throttle.RecordFailure("alice");
        Assert.True(throttle.IsBlocked("alice"));
        Assert.False(throttle.IsBlocked("bob"));

        now += TimeSpan.FromMinutes(11);
        Assert.False(throttle.IsBlocked("alice"));
    }

    [Fact]
    public void Throttle_ResetClearsFailures()
    {
        LoginThrottle throttle = new(2, TimeSpan.FromMinutes(10));
        throttle.RecordFailure("alice");
        throttle.RecordFailure("alice");
        throttle.Reset("alice");

        Assert.False(throttle.IsBlocked("alice"));
    }

    // ---- 保存 ----

    [Fact]
    public void UserStore_RoundTripsAndSeesExternalUpdates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sebastian-users-" + Guid.NewGuid().ToString("N"));
        try
        {
            UserStore store = new(directory);
            Assert.True(store.Current.IsEmpty);

            store.Save(SampleData());

            Assert.Equal(2, store.Current.LocalUsers.Count);
            Assert.Equal(AccessRole.Admin, new UserStore(directory).Load().FindLocalUser("alice")!.Role);
            Assert.Contains("\"admin\"", File.ReadAllText(Path.Combine(directory, UserStore.FileName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UserStore_KeepsLastGoodDataWhenFileBecomesCorrupt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sebastian-users-" + Guid.NewGuid().ToString("N"));
        try
        {
            UserStore store = new(directory);
            store.Save(SampleData());
            Assert.False(store.Current.IsEmpty);

            File.WriteAllText(Path.Combine(directory, UserStore.FileName), "{ broken json that is longer");

            Assert.Equal(2, store.Current.LocalUsers.Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // ---- auth コマンド ----

    [Fact]
    public void AuthOptions_ParsesAddUserWithRole()
    {
        AuthOptions? options = AuthOptions.Parse(["add-user", "carol", "/repo", "--role", "admin"]);

        Assert.NotNull(options);
        Assert.Equal(AuthAction.AddUser, options.Action);
        Assert.Equal("carol", options.Value);
        Assert.Equal("/repo", options.RepositoryPath);
        Assert.Equal(AccessRole.Admin, options.Role);
    }

    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("add-user")]
    [InlineData("add-user", "carol", "--role", "root")]
    [InlineData("add-user", "carol", "--role", "7")]
    public void AuthOptions_RejectsInvalid(params string[] args)
        => Assert.Null(AuthOptions.Parse(args));

    [Fact]
    public void AuthCommand_NormalizesAndDeduplicatesGoogleEntries()
    {
        UserData data = new();
        Assert.Null(AuthCommand.Apply(AuthOptions.Parse(["add-admin", "Boss@Example.co.jp"])!, data));
        Assert.Null(AuthCommand.Apply(AuthOptions.Parse(["add-admin", "boss@example.co.jp"])!, data));
        Assert.Null(AuthCommand.Apply(AuthOptions.Parse(["add-domain", "@Example.co.jp"])!, data));

        Assert.Equal(["boss@example.co.jp"], data.GoogleAdmins);
        Assert.Equal(["example.co.jp"], data.GoogleDomains);
    }

    [Theory]
    [InlineData("add-admin", "not-an-email")]
    [InlineData("add-domain", "*.example.com")]
    [InlineData("add-domain", "localhost")]
    public void AuthCommand_RejectsInvalidValues(string verb, string value)
        => Assert.NotNull(AuthCommand.Apply(AuthOptions.Parse([verb, value])!, new UserData()));

    [Fact]
    public void AuthCommand_RefusesToRemoveLastEntryOrLastAdminWithoutForce()
    {
        UserData data = new() { GoogleAdmins = ["boss@example.co.jp"] };
        Assert.NotNull(AuthCommand.Apply(AuthOptions.Parse(["remove-admin", "boss@example.co.jp"])!, data));

        UserData withDomain = new() { GoogleAdmins = ["boss@example.co.jp"], GoogleDomains = ["example.co.jp"] };
        Assert.NotNull(AuthCommand.Apply(AuthOptions.Parse(["remove-admin", "boss@example.co.jp"])!, withDomain));

        UserData forced = new() { GoogleAdmins = ["boss@example.co.jp"] };
        Assert.Null(AuthCommand.Apply(AuthOptions.Parse(["remove-admin", "boss@example.co.jp", "--force"])!, forced));
        Assert.Empty(forced.GoogleAdmins);
    }

    [Fact]
    public void AuthCommand_RemoveUnknownReportsError()
        => Assert.NotNull(AuthCommand.Apply(
            AuthOptions.Parse(["remove-user", "ghost"])!, new UserData { GoogleAdmins = ["a@b.co"] }));

    [Fact]
    public void Policy_AutoApproveIsAdminOnly()
    {
        Assert.True(AccessPolicy.CanAutoApprove(AccessRole.Admin));
        Assert.False(AccessPolicy.CanAutoApprove(AccessRole.User));
        Assert.False(AccessPolicy.CanAutoApprove(null));
    }

    [Fact]
    public void ThrottleKey_IsPerUserAndPerIp_SoOthersCannotLockOutTheOwner()
    {
        LoginThrottle throttle = new(maxFailures: 3, window: TimeSpan.FromMinutes(10));
        string attacker = AccessControl.ThrottleKey("alice", "203.0.113.9");
        string owner = AccessControl.ThrottleKey("Alice ", "198.51.100.7");

        for (int i = 0; i < 3; i++) throttle.RecordFailure(attacker);

        Assert.True(throttle.IsBlocked(attacker));
        Assert.False(throttle.IsBlocked(owner));
        Assert.Equal(AccessControl.ThrottleKey("alice", "198.51.100.7"), owner);
    }

    private static Microsoft.AspNetCore.Http.HttpRequest Request(string method, string host, string? origin)
    {
        Microsoft.AspNetCore.Http.DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context.Request;
    }

    [Theory]
    [InlineData("POST", "dash.example.com", "https://dash.example.com", true)]
    [InlineData("POST", "dash.example.com", "https://evil.example.net", false)]
    [InlineData("POST", "dash.example.com", "null", false)]
    [InlineData("POST", "dash.example.com", null, true)]
    [InlineData("GET", "dash.example.com", "https://evil.example.net", true)]
    public void IsSameOriginForWrite_RejectsForeignOriginOnWrites(string method, string host, string? origin, bool expected)
        => Assert.Equal(expected, AccessControl.IsSameOriginForWrite(Request(method, host, origin)));

    private sealed class FakeWebSocketFeature : Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(Microsoft.AspNetCore.Http.WebSocketAcceptContext context)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("https://dash.example.com", true)]
    [InlineData("https://evil.example.net", false)]
    [InlineData(null, true)]
    public void IsSameOriginForWrite_AppliesToWebSocketUpgrades(string? origin, bool expected)
    {
        Microsoft.AspNetCore.Http.HttpRequest request = Request("GET", "dash.example.com", origin);
        request.HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature>(new FakeWebSocketFeature());

        Assert.Equal(expected, AccessControl.IsSameOriginForWrite(request));
    }

    [Theory]
    [InlineData("pbkdf2-sha256$1000$$")]
    [InlineData("pbkdf2-sha256$1000$AAAAAAAAAAAAAAAAAAAAAA==$")]
    [InlineData("pbkdf2-sha256$1000$$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void PasswordHasher_RejectsEmptyOrShortSaltAndHash(string stored)
        => Assert.False(PasswordHasher.Verify("", stored) || PasswordHasher.Verify("anything", stored));

    [Fact]
    public void PasswordHasher_StillVerifiesHashesCreatedWithFewerIterations()
    {
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        byte[] hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "old-password-123", salt, 210_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        string stored = $"pbkdf2-sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

        Assert.True(PasswordHasher.Verify("old-password-123", stored));
        Assert.False(PasswordHasher.Verify("wrong-password", stored));
    }
}
