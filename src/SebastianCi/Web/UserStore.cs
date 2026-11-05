using System.Text.Json;
using System.Text.Json.Serialization;

namespace SebastianCi.Web;

/// <summary>アクセス権限。Admin は設定の編集まで、User は閲覧と実行まで。</summary>
public enum AccessRole { User = 1, Admin = 2 }

public sealed record LocalUser(string Username, string PasswordHash, AccessRole Role);

/// <summary>
/// 事前登録されたアカウント情報。
/// ・LocalUsers: ユーザー名 + パスワードでログインするアカウント（ロールは個別に指定）
/// ・GoogleAdmins: Google ログインで管理者になるメールアドレス
/// ・GoogleDomains: Google ログインで一般ユーザー（閲覧と実行）になる会社ドメイン
/// </summary>
public sealed class UserData
{
    public List<LocalUser> LocalUsers { get; set; } = [];
    public List<string> GoogleAdmins { get; set; } = [];
    public List<string> GoogleDomains { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => LocalUsers.Count == 0 && GoogleAdmins.Count == 0 && GoogleDomains.Count == 0;

    /// <summary>ユーザー名とパスワードを検証する。失敗時は null。</summary>
    public AccessRole? Authenticate(string username, string password)
    {
        LocalUser? user = FindLocalUser(username);
        if (user is null)
        {
            PasswordHasher.SpendVerificationTime(password);
            return null;
        }

        return PasswordHasher.Verify(password, user.PasswordHash) ? user.Role : null;
    }

    /// <summary>
    /// Google でログインした人のロールを決める。メールが Google 側で確認済みであることが前提。
    /// 管理者リストに載っていれば Admin、会社ドメインに属していれば User、どちらでもなければ null。
    /// </summary>
    public AccessRole? ResolveGoogle(string? email, bool emailVerified)
    {
        string? normalized = NormalizeEmail(email);
        if (!emailVerified || normalized is null) return null;

        if (GoogleAdmins.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return AccessRole.Admin;

        string domain = normalized[(normalized.LastIndexOf('@') + 1)..];
        return GoogleDomains.Contains(domain, StringComparer.OrdinalIgnoreCase) ? AccessRole.User : null;
    }

    /// <summary>発行済みセッションが今も有効か（アカウント削除・ロール変更に追随するため）を再判定する。</summary>
    public AccessRole? ResolveSession(string kind, string name)
        => kind == AccessControl.GoogleKind ? ResolveGoogle(name, emailVerified: true) : FindLocalUser(name)?.Role;

    public LocalUser? FindLocalUser(string username)
        => LocalUsers.FirstOrDefault(user => user.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeEmail(string? email)
    {
        string? trimmed = email?.Trim().ToLowerInvariant();
        if (trimmed is null) return null;

        int at = trimmed.LastIndexOf('@');
        return at > 0 && at < trimmed.Length - 1 && !trimmed.Contains(' ') ? trimmed : null;
    }

    public static string? NormalizeDomain(string? domain)
    {
        string? trimmed = domain?.Trim().TrimStart('@').ToLowerInvariant();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Contains('.') && !trimmed.Contains(' ')
            && !trimmed.Contains('@') && !trimmed.Contains('*')
            ? trimmed
            : null;
    }
}

/// <summary>users.json の読み書き。ファイルの更新を検知して再読み込みする（serve 稼働中の `auth` コマンドに追随）。</summary>
public sealed class UserStore
{
    public const string FileName = "users.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly object _syncRoot = new();
    private UserData? _cached;
    private (DateTime WriteTime, long Length) _stamp;

    public UserStore(string dataRootPath) => _path = Path.Combine(dataRootPath, FileName);

    /// <summary>最新の内容。読み込みに失敗した場合は直前に読めた内容を返す（無ければ空）。</summary>
    public UserData Current
    {
        get
        {
            lock (_syncRoot)
            {
                (DateTime, long) stamp = GetStamp();
                if (_cached is null || stamp != _stamp)
                {
                    _cached = TryRead() ?? _cached ?? new UserData();
                    _stamp = stamp;
                }

                return _cached;
            }
        }
    }

    /// <summary>CLI 用。壊れたファイルは握りつぶさず例外にして、上書きによる登録情報の消失を防ぐ。</summary>
    public UserData Load()
        => File.Exists(_path)
            ? JsonSerializer.Deserialize<UserData>(File.ReadAllText(_path), JsonOptions) ?? new UserData()
            : new UserData();

    public void Save(UserData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }

    private (DateTime, long) GetStamp()
    {
        FileInfo file = new(_path);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1);
    }

    private UserData? TryRead()
    {
        try
        {
            return File.Exists(_path) ? Load() : new UserData();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
