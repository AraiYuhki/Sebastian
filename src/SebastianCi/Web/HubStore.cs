using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SebastianCi.Web;

/// <summary>
/// ハブに登録された1件。次の2種類がある。
/// ・リポジトリ型: Path は Git リポジトリ本体。CloneUrl があるものはハブが clone した作業コピー。
/// ・ワークスペース型ジョブ（Sources が null でない）: ハブが jobs/&lt;id&gt;/ に作業場を持ち、実行のたびに Sources を取り込む。
///   Path はワークスペース、ConfigFileName は定義ファイルの絶対パス（リポジトリには何も書き込まない）。
/// </summary>
public sealed record RepositoryEntry(
    string Id, string Name, string Path, string ConfigFileName, string? CloneUrl = null,
    List<SourceEntry>? Sources = null)
{
    public bool IsWorkspaceJob => Sources is not null;
}

/// <summary>画面・API から受け取るソース1件（未検証）。</summary>
public sealed record SourcePayload(string? Url, string? Branch, string? Directory);

/// <summary>
/// ジョブ間の連鎖。From の実行が終わったとき On の条件（success / failure / always）を満たしていれば、To を実行する。
/// Job を指定すると To のそのジョブ（と依存）だけを実行する。Rebuild は実行済みコミットでも再実行する（既定）。
/// Yes は承認ゲートの自動承認で、設定できるのは管理者だけ（連鎖の編集自体が管理者のみ）。
/// </summary>
public sealed record ChainEntry(
    string Id, string From, string To, string On, string? Job = null,
    Dictionary<string, string>? Params = null, bool Rebuild = true, bool Yes = false, bool Enabled = true);

/// <summary>画面・API から受け取る連鎖の入力（未検証）。更新のときは null の項目を「変更なし」として扱う。</summary>
public sealed record ChainPayload(
    string? From, string? To, string? On, string? Job, Dictionary<string, string>? Params,
    bool? Rebuild, bool? Yes, bool? Enabled);

/// <summary>hub.json の中身。</summary>
public sealed class HubData
{
    public List<RepositoryEntry> Repositories { get; set; } = [];
    public List<ChainEntry> Chains { get; set; } = [];
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
    public const int MaxSources = 10;
    public const int MaxChains = 200;
    public const int MaxChainParams = 20;

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
    public static string CreateId(string name, IEnumerable<string> existingIds, string fallback = "repo")
    {
        StringBuilder builder = new();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        string baseId = builder.ToString().Trim('-');
        if (baseId.Length > 32) baseId = baseId[..32].Trim('-');
        if (baseId.Length == 0) baseId = fallback;

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

    private static readonly Regex DirectoryPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,59}$", RegexOptions.Compiled);
    private static readonly Regex BranchPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$", RegexOptions.Compiled);

    /// <summary>
    /// ワークスペース型ジョブのソースを検証し、正規化（空白除去・配置先の既定値補完）して返す。
    /// 配置先はワークスペース直下の1階層の名前（"." は単一ソースのときだけ）で、重複できない。
    /// </summary>
    public static (List<SourceEntry>? Sources, string? Error) NormalizeSources(IReadOnlyList<SourcePayload>? payloads)
    {
        List<SourcePayload> raw = (payloads ?? []).Where(p => p is not null).ToList();
        if (raw.Count > MaxSources) return (null, $"ソースは {MaxSources} 件までです。");

        List<SourceEntry> result = [];
        foreach (SourcePayload payload in raw)
        {
            string? urlError = GitCloner.ValidateUrl(payload.Url);
            if (urlError is not null) return (null, urlError);

            string url = payload.Url!.Trim();
            string? branch = string.IsNullOrWhiteSpace(payload.Branch) ? null : payload.Branch.Trim();
            if (branch is not null
                && (!BranchPattern.IsMatch(branch) || branch.Contains("..") || branch.EndsWith('/')
                    || branch.EndsWith(".lock", StringComparison.Ordinal)))
            {
                return (null, $"ブランチ名の形式が正しくありません: {branch}");
            }

            string directory = string.IsNullOrWhiteSpace(payload.Directory) ? DeriveDirectory(url, result.Select(s => s.Directory)) : payload.Directory.Trim();
            if (directory != "." && !DirectoryPattern.IsMatch(directory))
            {
                return (null, "配置先には英数字・. _ - だけの1階層の名前を指定してください（. で始められません）。");
            }

            if (result.Any(existing => string.Equals(existing.Directory, directory, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, $"配置先「{directory}」が重複しています。");
            }

            result.Add(new SourceEntry(url, branch, directory));
        }

        return result.Any(s => s.Directory == ".") && result.Count > 1
            ? (null, "配置先に「.」を指定できるのは、ソースが1件だけのときです。")
            : (result, null);
    }

    /// <summary>URL の末尾（リポジトリ名）から配置先の既定名を作る。重複するときは連番を付ける。</summary>
    private static string DeriveDirectory(string url, IEnumerable<string> taken)
    {
        string last = url.TrimEnd('/').Split('/', ':').LastOrDefault() ?? "";
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        string name = new(last.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-').ToArray());
        name = name.TrimStart('.', '-', '_');
        if (name.Length == 0) name = "source";
        if (name.Length > 40) name = name[..40];

        HashSet<string> used = new(taken, StringComparer.OrdinalIgnoreCase);
        string candidate = name;
        for (int suffix = 2; used.Contains(candidate); suffix++) candidate = $"{name}-{suffix}";
        return candidate;
    }

    public static readonly string[] ChainConditions = ["success", "failure", "always"];
    private static readonly Regex JobNamePattern = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex ParamNamePattern = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    /// <summary>連鎖の条件・ジョブ名・パラメーターの形式を検証する（登録先の存在や循環は HubService が見る）。</summary>
    public static string? ValidateChainFields(string on, string? job, Dictionary<string, string>? parameters)
    {
        if (!ChainConditions.Contains(on)) return "条件は success（成功したら）/ failure（失敗したら）/ always（完了したら）のいずれかです。";
        if (job is not null && !JobNamePattern.IsMatch(job)) return "ジョブ名に使えるのは英数字と . - _ だけです。";
        if (parameters is null) return null;

        if (parameters.Count > MaxChainParams) return $"パラメーターは {MaxChainParams} 件までです。";
        foreach ((string name, string value) in parameters)
        {
            if (!ParamNamePattern.IsMatch(name)) return $"パラメーター名の形式が正しくありません: {name}";
            if (value.Length > 1000 || value.Any(char.IsControl)) return $"パラメーター「{name}」の値が長すぎるか、制御文字を含んでいます。";
        }

        return null;
    }

    /// <summary>from → to の連鎖を足すと、連鎖がぐるぐる回る（循環する）か。ignoreId の連鎖は無いものとして数える（更新用）。</summary>
    public static bool WouldCreateCycle(IEnumerable<ChainEntry> chains, string from, string to, string? ignoreId = null)
    {
        List<ChainEntry> edges = chains.Where(chain => chain.Id != ignoreId).ToList();
        HashSet<string> visited = [];
        Stack<string> pending = new([to]);
        while (pending.TryPop(out string? node))
        {
            if (node == from) return true;
            if (!visited.Add(node)) continue;
            foreach (ChainEntry edge in edges.Where(chain => chain.From == node)) pending.Push(edge.To);
        }

        return false;
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
