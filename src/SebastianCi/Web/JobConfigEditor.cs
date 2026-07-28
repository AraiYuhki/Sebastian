using SebastianCi.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SebastianCi.Web;

/// <summary>ダッシュボードのジョブ一覧表示用スナップショット。</summary>
public sealed record JobsSnapshot(List<string> Stages, List<JobSummary> Jobs);

/// <summary>フォームで編集できるジョブ1件分の情報。フォーム対象外のキーは AdvancedKeys に名前だけ載せる。</summary>
public sealed record JobSummary(
    string Name, string Image, string Stage, List<string> Needs, List<string> Script,
    Dictionary<string, string> Env, List<string> Artifacts, int Timeout, int Retry,
    bool ContinueOnError, bool Shell, List<string> AdvancedKeys,
    List<string>? AfterScript = null, string? Approval = null, List<string>? Reports = null,
    bool Remote = false, List<string>? Labels = null);

/// <summary>ダッシュボードのフォームから送られてくるジョブ1件分の入力。</summary>
public sealed record JobFormPayload(
    string? OriginalName, string Name, string? Image, string? Stage,
    List<string>? Needs, List<string>? Script, Dictionary<string, string>? Env,
    List<string>? Artifacts, int? Timeout, int? Retry, bool? ContinueOnError, bool? Shell,
    List<string>? AfterScript = null, string? Approval = null, List<string>? Reports = null,
    bool? Remote = null, List<string>? Labels = null);

/// <summary>
/// 設定ファイル（YAMLテキスト）の jobs セクションをジョブ単位で編集する。
/// 対象ジョブのブロックだけを差し替え、コメントや他のセクションの行はそのまま保持する。
/// フォームが扱わないキー（matrix / cache / changes / agent など）は既存の値を自動で引き継ぐ。
/// </summary>
public static class JobConfigEditor
{
    private const string SectionKey = "jobs:";

    /// <summary>フォームで編集できるキー。これ以外は「追加設定」としてそのまま保持される。</summary>
    private static readonly string[] FormKeys =
    [
        "image", "stage", "needs", "script", "env", "artifacts", "timeout", "retry", "continueOnError", "shell",
        "afterScript", "approval", "reports", "remote", "labels"
    ];

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithIndentedSequences()
        .Build();

    /// <summary>ステージ一覧と、フォーム編集用のジョブ一覧を読み出す。</summary>
    public static JobsSnapshot Read(string yaml)
    {
        RawPipeline raw = Parse(yaml);
        List<JobSummary> jobs = raw.Jobs.Select(pair => ToSummary(pair.Key, pair.Value ?? new())).ToList();
        return new JobsSnapshot(raw.Stages, jobs);
    }

    /// <summary>ジョブを追加または更新したYAMLを返す。名前変更時は他ジョブの needs 参照も追随させる。</summary>
    public static string Upsert(string yaml, JobFormPayload payload)
    {
        string originalName = string.IsNullOrWhiteSpace(payload.OriginalName) ? payload.Name : payload.OriginalName!;
        Dictionary<string, object> advanced = ReadAdvancedValues(yaml, originalName);
        string result = UpsertBlock(yaml, originalName, indent => BuildJobBlock(payload, advanced, indent));
        return originalName == payload.Name ? result : RenameNeedsReferences(result, originalName, payload.Name);
    }

    /// <summary>キー→値のマップからジョブを追加・置換したYAMLを返す（ビルドテンプレート用）。</summary>
    public static string UpsertMap(string yaml, string jobName, Dictionary<string, object> jobMap)
        => UpsertBlock(yaml, jobName, indent => BuildBlockLines(jobName, jobMap, indent));

    private static string UpsertBlock(string yaml, string jobName, Func<string, List<string>> buildBlock)
    {
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return AppendNewSection(lines, buildBlock("  "));

        EnsureBlockStyle(lines[sectionStart]);
        string childIndent = DetectChildIndent(lines, sectionStart, sectionEnd);
        (int jobStart, int jobEnd) = FindJobBlock(lines, sectionStart, sectionEnd, jobName, childIndent);
        List<string> block = buildBlock(childIndent);

        if (jobStart < 0)
        {
            int insertAt = sectionEnd;
            while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
            lines.InsertRange(insertAt, block);
        }
        else
        {
            lines.RemoveRange(jobStart, jobEnd - jobStart);
            lines.InsertRange(jobStart, block);
        }

        return string.Join('\n', lines);
    }

    /// <summary>指定した名前のジョブのブロックを削除したYAMLを返す。</summary>
    public static string Remove(string yaml, string name)
    {
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return yaml;

        EnsureBlockStyle(lines[sectionStart]);
        string childIndent = DetectChildIndent(lines, sectionStart, sectionEnd);
        (int jobStart, int jobEnd) = FindJobBlock(lines, sectionStart, sectionEnd, name, childIndent);
        if (jobStart < 0) return yaml;

        lines.RemoveRange(jobStart, jobEnd - jobStart);
        return string.Join('\n', lines);
    }

    private static string AppendNewSection(List<string> lines, List<string> block)
    {
        List<string> result = lines.ToList();
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        if (result.Count > 0) result.Add("");
        result.Add(SectionKey);
        result.AddRange(block);
        return string.Join('\n', result) + "\n";
    }

    /// <summary>フォームの入力＋引き継ぐ追加設定から、ジョブ1件分のYAML行を組み立てる。</summary>
    private static List<string> BuildJobBlock(
        JobFormPayload payload, Dictionary<string, object> advanced, string childIndent)
    {
        Dictionary<string, object> map = new();
        if (!string.IsNullOrWhiteSpace(payload.Image)) map["image"] = payload.Image.Trim();
        if (!string.IsNullOrWhiteSpace(payload.Stage)) map["stage"] = payload.Stage.Trim();
        if (payload.Needs is { Count: > 0 }) map["needs"] = payload.Needs;
        map["script"] = payload.Script ?? new List<string>();
        if (payload.AfterScript is { Count: > 0 }) map["afterScript"] = payload.AfterScript;
        if (payload.Env is { Count: > 0 }) map["env"] = payload.Env;
        if (payload.Artifacts is { Count: > 0 }) map["artifacts"] = payload.Artifacts;
        if (payload.Reports is { Count: > 0 }) map["reports"] = payload.Reports;
        if (payload.Timeout is > 0) map["timeout"] = payload.Timeout.Value;
        if (payload.Retry is > 0) map["retry"] = payload.Retry.Value;
        if (payload.ContinueOnError == true) map["continueOnError"] = true;
        if (payload.Shell == true) map["shell"] = true;
        if (!string.IsNullOrWhiteSpace(payload.Approval)) map["approval"] = payload.Approval.Trim();
        if (payload.Remote == true) map["remote"] = true;
        if (payload.Remote == true && payload.Labels is { Count: > 0 }) map["labels"] = payload.Labels;
        foreach ((string key, object value) in advanced) map[key] = value;

        return BuildBlockLines(payload.Name, map, childIndent);
    }

    private static List<string> BuildBlockLines(string jobName, Dictionary<string, object> map, string childIndent)
    {
        string contentIndent = childIndent + "  ";
        List<string> block = [$"{childIndent}{jobName}:"];
        block.AddRange(SplitLines(Serializer.Serialize(map).TrimEnd('\n')).Select(line => contentIndent + line));
        return block;
    }

    /// <summary>編集対象ジョブの、フォームが扱わないキーの値（matrix など）をそのまま読み出す。</summary>
    private static Dictionary<string, object> ReadAdvancedValues(string yaml, string jobName)
    {
        RawPipeline raw = Parse(yaml);
        if (!raw.Jobs.TryGetValue(jobName, out Dictionary<string, object>? job) || job is null) return new();

        return job.Where(pair => !FormKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>ジョブの名前変更時に、他ジョブの needs（ブロック形式・インライン形式の両方）を追随させる。</summary>
    private static string RenameNeedsReferences(string yaml, string oldName, string newName)
    {
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return yaml;

        int needsIndent = -1;
        for (int index = sectionStart + 1; index < sectionEnd; index++)
        {
            string trimmed = lines[index].TrimStart();
            int indent = lines[index].Length - trimmed.Length;
            if (needsIndent >= 0 && !string.IsNullOrWhiteSpace(lines[index]) && indent <= needsIndent) needsIndent = -1;
            if (needsIndent >= 0 && trimmed.StartsWith("- ", StringComparison.Ordinal)
                && trimmed[1..].Trim().Trim('"', '\'') == oldName)
            {
                lines[index] = new string(' ', indent) + "- " + newName;
                continue;
            }

            if (trimmed == "needs:") needsIndent = indent;
            else if (trimmed.StartsWith("needs:", StringComparison.Ordinal))
            {
                lines[index] = RenameInlineNeeds(lines[index], oldName, newName);
            }
        }

        return string.Join('\n', lines);
    }

    private static string RenameInlineNeeds(string line, string oldName, string newName)
    {
        int open = line.IndexOf('[');
        int close = line.LastIndexOf(']');
        if (open < 0 || close < open) return line;

        IEnumerable<string> items = line[(open + 1)..close].Split(',')
            .Select(item => item.Trim())
            .Select(item => item.Trim('"', '\'') == oldName ? newName : item);
        return line[..(open + 1)] + string.Join(", ", items) + line[close..];
    }

    private static (int Start, int End) FindSectionRange(List<string> lines)
    {
        int start = lines.FindIndex(line => line.StartsWith(SectionKey, StringComparison.Ordinal));
        if (start < 0) return (-1, -1);

        for (int index = start + 1; index < lines.Count; index++)
        {
            if (IsTopLevelKey(lines[index])) return (start, index);
        }

        return (start, lines.Count);
    }

    private static bool IsTopLevelKey(string line)
        => line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#');

    private static void EnsureBlockStyle(string sectionLine)
    {
        string rest = sectionLine[SectionKey.Length..].Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            throw new InvalidPipelineException(
                "jobs がインライン形式（jobs: {...}）のため自動編集できません。設定エディタで直接編集してください。");
        }
    }

    /// <summary>ジョブ名行のインデントを最初の子要素から検出する（子要素が無ければ2スペース）。</summary>
    private static string DetectChildIndent(List<string> lines, int sectionStart, int sectionEnd)
    {
        for (int index = sectionStart + 1; index < sectionEnd; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            return line[..(line.Length - line.TrimStart().Length)];
        }

        return "  ";
    }

    /// <summary>指定した名前のジョブのブロック範囲（名前行から、次のジョブ名行または区切りまで）を返す。</summary>
    private static (int Start, int End) FindJobBlock(
        List<string> lines, int sectionStart, int sectionEnd, string name, string childIndent)
    {
        for (int index = sectionStart + 1; index < sectionEnd; index++)
        {
            if (!TryGetJobName(lines[index], childIndent, out string? jobName) || jobName != name) continue;

            int end = index + 1;
            while (end < sectionEnd
                   && (string.IsNullOrWhiteSpace(lines[end]) || GetIndentLength(lines[end]) > childIndent.Length))
            {
                end++;
            }

            while (end > index + 1 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
            return (index, end);
        }

        return (-1, -1);
    }

    private static bool TryGetJobName(string line, string childIndent, out string? name)
    {
        name = null;
        if (!line.StartsWith(childIndent, StringComparison.Ordinal)) return false;

        string rest = line[childIndent.Length..];
        if (rest.Length == 0 || char.IsWhiteSpace(rest[0]) || rest.StartsWith('#')) return false;

        int colon = rest.IndexOf(':');
        if (colon <= 0) return false;

        name = rest[..colon].Trim().Trim('"', '\'');
        return true;
    }

    private static int GetIndentLength(string line) => line.Length - line.TrimStart().Length;

    private static List<string> SplitLines(string yaml) => yaml.Replace("\r\n", "\n").Split('\n').ToList();

    private static RawPipeline Parse(string yaml) => Deserializer.Deserialize<RawPipeline?>(yaml) ?? new RawPipeline();

    private static JobSummary ToSummary(string name, Dictionary<string, object> job) => new(
        name,
        GetString(job, "image"),
        GetString(job, "stage"),
        GetStringList(job, "needs"),
        GetStringList(job, "script"),
        GetStringMap(job, "env"),
        GetStringList(job, "artifacts"),
        GetInt(job, "timeout"),
        GetInt(job, "retry"),
        GetBool(job, "continueOnError"),
        GetBool(job, "shell"),
        job.Keys.Where(key => !FormKeys.Contains(key)).ToList(),
        GetStringList(job, "afterScript"),
        GetString(job, "approval"),
        GetStringList(job, "reports"),
        GetBool(job, "remote"),
        GetStringList(job, "labels"));

    private static string GetString(Dictionary<string, object> job, string key)
        => job.TryGetValue(key, out object? value) && value is string text ? text : "";

    private static List<string> GetStringList(Dictionary<string, object> job, string key)
        => job.TryGetValue(key, out object? value) && value is List<object> list
            ? list.Select(item => item?.ToString() ?? "").ToList()
            : new List<string>();

    private static Dictionary<string, string> GetStringMap(Dictionary<string, object> job, string key)
        => job.TryGetValue(key, out object? value) && value is Dictionary<object, object> map
            ? map.ToDictionary(pair => pair.Key.ToString() ?? "", pair => pair.Value?.ToString() ?? "")
            : new Dictionary<string, string>();

    private static int GetInt(Dictionary<string, object> job, string key)
        => job.TryGetValue(key, out object? value) && value is string text && int.TryParse(text, out int number)
            ? number
            : 0;

    private static bool GetBool(Dictionary<string, object> job, string key)
        => job.TryGetValue(key, out object? value) && value is string text
            && bool.TryParse(text, out bool flag) && flag;

    /// <summary>ステージと jobs 配下だけを寛容に読み取るためのDTO。他のキーの不備には影響されない。</summary>
    private sealed class RawPipeline
    {
        public List<string> Stages { get; set; } = new();
        public Dictionary<string, Dictionary<string, object>?> Jobs { get; set; } = new();
    }
}
