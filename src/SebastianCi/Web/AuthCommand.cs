using SebastianCi.Core;

namespace SebastianCi.Web;

public enum AuthAction { List, AddUser, RemoveUser, AddAdmin, RemoveAdmin, AddDomain, RemoveDomain }

/// <summary>`auth` サブコマンドの引数。`auth &lt;操作&gt; [値] [--role admin|user] [--data-dir P] [--force] [リポジトリ]`。</summary>
public sealed record AuthOptions(
    AuthAction Action, string? Value, AccessRole Role, string RepositoryPath, string? DataDirectoryPath, bool Force,
    bool Hub = false)
{
    public const string PasswordEnvironmentVariable = "SEBASTIAN_CI_PASSWORD";

    public const string Usage = """
        使い方: sebastian-ci auth <操作> [値] [リポジトリパス] [--role admin|user] [--data-dir <パス>] [--force] [--hub]
          list                         登録内容を表示する
          add-user <名前> [--role R]   ユーザー名+パスワードのアカウントを追加/パスワード更新（既定は user）
          remove-user <名前>           アカウントを削除する
          add-admin <メール>           Google ログインで管理者になるメールアドレスを登録する
          remove-admin <メール>        管理者のメールアドレスを削除する
          add-domain <ドメイン>        Google ログインで一般ユーザー(閲覧・実行)になる会社ドメインを登録する
          remove-domain <ドメイン>     ドメインを削除する
        パスワードは対話入力。非対話では環境変数 SEBASTIAN_CI_PASSWORD で渡せます。
        """;

    public static AuthOptions? Parse(string[] args)
    {
        if (args.Length == 0 || ParseAction(args[0]) is not { } action) return null;

        string? value = null;
        string repository = ".";
        string? dataDirectory = null;
        AccessRole role = AccessRole.User;
        bool force = false;
        bool hub = false;
        bool needsValue = action != AuthAction.List;

        Queue<string> remaining = new(args[1..]);
        while (remaining.TryDequeue(out string? argument))
        {
            switch (argument)
            {
                case "--role":
                    if (!remaining.TryDequeue(out string? roleText)
                        || !Enum.TryParse(roleText, ignoreCase: true, out role)
                        || !Enum.IsDefined(role))
                    {
                        return null;
                    }

                    break;
                case "--data-dir":
                    if (!remaining.TryDequeue(out dataDirectory)) return null;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--hub":
                    hub = true;
                    break;
                default:
                    if (argument.StartsWith('-')) return null;
                    if (needsValue && value is null) value = argument;
                    else repository = argument;
                    break;
            }
        }

        return needsValue && value is null ? null : new AuthOptions(action, value, role, repository, dataDirectory, force, hub);
    }

    private static AuthAction? ParseAction(string text)
        => text switch
        {
            "list" => AuthAction.List,
            "add-user" => AuthAction.AddUser,
            "remove-user" => AuthAction.RemoveUser,
            "add-admin" => AuthAction.AddAdmin,
            "remove-admin" => AuthAction.RemoveAdmin,
            "add-domain" => AuthAction.AddDomain,
            "remove-domain" => AuthAction.RemoveDomain,
            _ => null
        };
}

/// <summary>`auth` サブコマンドの実装。users.json を直接更新する（serve 稼働中でも次のリクエストから反映される）。</summary>
public static class AuthCommand
{
    private static readonly System.Text.RegularExpressions.Regex UsernamePattern = new("^[A-Za-z0-9_.@-]{1,64}$");

    public static int Run(AuthOptions options)
    {
        ServeOptions serve = new(options.RepositoryPath, 0, options.DataDirectoryPath, "", Hub: options.Hub);
        UserStore store = new(serve.ResolveDataRootPath());
        UserData data;
        try
        {
            data = store.Load();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            ConsoleLogger.WriteError($"💥 {UserStore.FileName} を読めません。壊れている場合は手で修正してください: {exception.Message}");
            return 1;
        }

        if (options.Action == AuthAction.List)
        {
            Print(data);
            return 0;
        }

        string? error = Apply(options, data);
        if (error is not null)
        {
            ConsoleLogger.WriteError($"💥 {error}");
            return 1;
        }

        store.Save(data);
        ConsoleLogger.WriteSuccess("✅ 登録内容を更新しました。実行中の serve にも次のリクエストから反映されます。");
        return 0;
    }

    /// <summary>操作を data に適用する。失敗時はエラーメッセージを返す。</summary>
    public static string? Apply(AuthOptions options, UserData data)
    {
        string value = options.Value ?? "";
        UserData before = Clone(data);
        string? error = options.Action switch
        {
            AuthAction.AddUser => AddUser(data, value, options.Role),
            AuthAction.RemoveUser => data.LocalUsers.RemoveAll(u => u.Username.Equals(value, StringComparison.OrdinalIgnoreCase)) > 0
                ? null : $"ユーザー「{value}」は登録されていません。",
            AuthAction.AddAdmin => AddUnique(data.GoogleAdmins, UserData.NormalizeEmail(value), "メールアドレスの形式が正しくありません。"),
            AuthAction.RemoveAdmin => Remove(data.GoogleAdmins, UserData.NormalizeEmail(value)),
            AuthAction.AddDomain => AddUnique(data.GoogleDomains, UserData.NormalizeDomain(value), "ドメインの形式が正しくありません（例: example.co.jp）。"),
            AuthAction.RemoveDomain => Remove(data.GoogleDomains, UserData.NormalizeDomain(value)),
            _ => null
        };
        if (error is not null) return error;

        bool removing = options.Action is AuthAction.RemoveUser or AuthAction.RemoveAdmin or AuthAction.RemoveDomain;
        if (removing && !options.Force)
        {
            if (data.IsEmpty && !before.IsEmpty)
            {
                return "最後の登録を削除すると認証が無効になり、誰でも操作できてしまいます。削除する場合は --force を付けてください。";
            }

            if (HasAdmin(before) && !HasAdmin(data))
            {
                return "管理者がいなくなります（設定の編集ができなくなります）。削除する場合は --force を付けてください。";
            }
        }

        return null;
    }

    private static bool HasAdmin(UserData data)
        => data.GoogleAdmins.Count > 0 || data.LocalUsers.Any(user => user.Role == AccessRole.Admin);

    private static UserData Clone(UserData data) => new()
    {
        LocalUsers = [.. data.LocalUsers],
        GoogleAdmins = [.. data.GoogleAdmins],
        GoogleDomains = [.. data.GoogleDomains]
    };

    private static string? AddUser(UserData data, string username, AccessRole role)
    {
        if (!UsernamePattern.IsMatch(username)) return "ユーザー名に使えるのは英数字と . _ @ - だけです（64文字まで）。";

        string? password = ReadPassword(username);
        if (password is null) return "パスワードが一致しませんでした。";
        if (password.Length < PasswordHasher.MinimumPasswordLength)
        {
            return $"パスワードは {PasswordHasher.MinimumPasswordLength} 文字以上にしてください。";
        }

        data.LocalUsers.RemoveAll(user => user.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        data.LocalUsers.Add(new LocalUser(username, PasswordHasher.Hash(password), role));
        return null;
    }

    private static string? AddUnique(List<string> list, string? normalized, string invalidMessage)
    {
        if (normalized is null) return invalidMessage;

        if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase)) list.Add(normalized);
        return null;
    }

    private static string? Remove(List<string> list, string? normalized)
        => normalized is not null && list.RemoveAll(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0
            ? null
            : "登録されていません。";

    private static string? ReadPassword(string username)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(AuthOptions.PasswordEnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;

        string first = ReadHidden($"「{username}」のパスワード: ");
        string second = ReadHidden("もう一度入力: ");
        return first == second ? first : null;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        System.Text.StringBuilder buffer = new();
        while (Console.ReadKey(intercept: true) is { } key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }

        Console.WriteLine();
        return buffer.ToString();
    }

    private static void Print(UserData data)
    {
        Console.WriteLine($"ローカルアカウント ({data.LocalUsers.Count})");
        foreach (LocalUser user in data.LocalUsers) Console.WriteLine($"  {user.Username}  [{user.Role.ToString().ToLowerInvariant()}]");
        Console.WriteLine($"Google 管理者 ({data.GoogleAdmins.Count})");
        foreach (string email in data.GoogleAdmins) Console.WriteLine($"  {email}");
        Console.WriteLine($"Google 許可ドメイン・一般ユーザー ({data.GoogleDomains.Count})");
        foreach (string domain in data.GoogleDomains) Console.WriteLine($"  @{domain}");
    }
}
