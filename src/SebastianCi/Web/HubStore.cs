using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SebastianCi.Web;

/// <summary>ハブに登録された1リポジトリ。CloneUrl があるものはハブが clone した作業コピー。</summary>
public sealed record RepositoryEntry(string Id, string Name, string Path, string ConfigFileName, string? CloneUrl = null);

/// <summary>hub.json の中身。</summary>
public sealed class HubData
{
    public List<RepositoryEntry> Repositories { get; set; } = [];
}

/// <summary>
/// 登録リポジトリ一覧（hub.json）の読み書きと、入力値の検証・ID採番。
/// 検証はここに集めて、Web・CLI のどちらから登録しても同じ規則になるようにする。
/// </summary>
public sealed class HubStore
{
    public const string FileName = "hub.json";
    public const int MaxNameLength = 60;
    public const int MaxRepositories = 200;

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public HubStore(string dataRootPath) => _path = System.IO.Path.Combine(dataRootPath, FileName);

    public HubData Load()
        => File.Exists(_path)
            ? JsonSerializer.Deserialize<HubData>(File.ReadAllText(_path), JsonOptions) ?? new HubData()
            : new HubData();

    public void Save(HubData data)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// <summary>表示名から URL に使える ID を作る。日本語など変換できない名前は "repo" を基に連番にする。</summary>
    public static string CreateId(string name, IEnumerable<string> existingIds)
    {
        StringBuilder builder = new();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        string baseId = builder.ToString().Trim('-');
        if (baseId.Length > 32) baseId = baseId[..32].Trim('-');
        if (baseId.Length == 0) baseId = "repo";

        HashSet<string> taken = new(existingIds, StringComparer.Ordinal);
        string candidate = baseId;
        for (int suffix = 2; taken.Contains(candidate); suffix++) candidate = $"{baseId}-{suffix}";
        return candidate;
    }

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "名前を入力してください。";
        if (name.Trim().Length > MaxNameLength) return $"名前は {MaxNameLength} 文字以内にしてください。";
        return name.Any(char.IsControl) ? "名前に制御文字は使えません。" : null;
    }

    /// <summary>設定ファイル名。リポジトリ直下のファイルに限る（パス区切りや .. を含めない）。</summary>
    public static string? ValidateConfigFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "設定ファイル名を入力してください。";
        if (fileName.Length > 100 || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")
            || fileName.Any(char.IsControl))
        {
            return "設定ファイル名にはパス区切り文字や .. を含められません。";
        }

        return fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? null
            : "設定ファイル名は .yaml または .yml で終わる名前にしてください。";
    }

    /// <summary>登録するローカルパスの検証。実在する Git リポジトリ（.git がある）であること。</summary>
    public static string? ValidateRepositoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "パスを入力してください。";
        if (path.Any(char.IsControl)) return "パスに制御文字は使えません。";

        string full;
        try { full = System.IO.Path.GetFullPath(path.Trim()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "パスの形式が正しくありません。";
        }

        if (!Directory.Exists(full)) return $"ディレクトリが見つかりません: {full}";

        string gitPath = System.IO.Path.Combine(full, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath) ? null : $"Git リポジトリではありません（.git がありません）: {full}";
    }

    /// <summary>同じ場所を二重に登録していないか（Windows では大文字小文字を区別しない）。</summary>
    public static bool IsSamePath(string a, string b)
        => string.Equals(
            System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'),
            System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
