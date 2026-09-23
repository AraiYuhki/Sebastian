using SebastianCi.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SebastianCi.Web;

/// <summary>ダッシュボード表示用のスケジュール1件分の情報。</summary>
public sealed record ScheduleSummary(
    string Id, string Cron, string? Job, Dictionary<string, string> Params, bool Yes, bool Rebuild, bool Enabled);

/// <summary>ダッシュボードのフォームから送られてくるスケジュール1件分の入力。</summary>
public sealed record ScheduleFormPayload(
    string? OriginalId, string Id, string Cron, string? Job,
    Dictionary<string, string>? Params, bool Yes, bool Rebuild, bool Enabled);

/// <summary>
/// 設定ファイル（YAMLテキスト）の schedules セクションをスケジュール単位で編集する。
/// 対象ブロックだけを差し替え、コメントや他のセクションの行はそのまま保持する。
/// </summary>
public static class ScheduleConfigEditor
{
    private const string SectionKey = "schedules:";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithIndentedSequences()
        .Build();

    /// <summary>schedules セクションだけを寛容に読み出す（他のキーの不備には影響されない）。</summary>
    public static List<ScheduleSummary> Read(string yaml)
    {
        RawRoot root = Deserializer.Deserialize<RawRoot?>(yaml) ?? new RawRoot();
        return root.Schedules.Select(pair => ToSummary(pair.Key, pair.Value ?? new())).ToList();
    }

    /// <summary>スケジュールを追加または更新したYAMLを返す。</summary>
    public static string Upsert(string yaml, ScheduleFormPayload payload)
    {
        string originalId = string.IsNullOrWhiteSpace(payload.OriginalId) ? payload.Id : payload.OriginalId!;
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return AppendNewSection(lines, BuildBlock(payload, "  "));

        EnsureBlockStyle(lines[sectionStart]);
        string childIndent = DetectChildIndent(lines, sectionStart, sectionEnd);
        (int blockStart, int blockEnd) = FindEntryBlock(lines, sectionStart, sectionEnd, originalId, childIndent);
        List<string> block = BuildBlock(payload, childIndent);

        if (blockStart < 0)
        {
            int insertAt = sectionEnd;
            while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
            lines.InsertRange(insertAt, block);
        }
        else
        {
            lines.RemoveRange(blockStart, blockEnd - blockStart);
            lines.InsertRange(blockStart, block);
        }

        return string.Join('\n', lines);
    }

    /// <summary>指定したIDのスケジュールを削除したYAMLを返す。最後の1件を消すとセクション行ごと取り除く。</summary>
    public static string Remove(string yaml, string id)
    {
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return yaml;

        EnsureBlockStyle(lines[sectionStart]);
        string childIndent = DetectChildIndent(lines, sectionStart, sectionEnd);
        (int blockStart, int blockEnd) = FindEntryBlock(lines, sectionStart, sectionEnd, id, childIndent);
        if (blockStart < 0) return yaml;

        bool wasLastEntry = Read(string.Join('\n', lines)).Count == 1;
        lines.RemoveRange(blockStart, blockEnd - blockStart);
        if (wasLastEntry) lines.RemoveAt(sectionStart);
        return string.Join('\n', lines);
    }

    private static List<string> BuildBlock(ScheduleFormPayload payload, string childIndent)
    {
        Dictionary<string, object> map = new() { ["cron"] = payload.Cron };
        if (!string.IsNullOrWhiteSpace(payload.Job)) map["job"] = payload.Job.Trim();
        if (payload.Params is { Count: > 0 }) map["params"] = payload.Params;
        if (payload.Yes) map["yes"] = true;
        if (payload.Rebuild) map["rebuild"] = true;
        if (!payload.Enabled) map["enabled"] = false;

        string contentIndent = childIndent + "  ";
        List<string> block = [$"{childIndent}{payload.Id}:"];
        block.AddRange(SplitLines(Serializer.Serialize(map).TrimEnd('\n')).Select(line => contentIndent + line));
        return block;
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

    private static ScheduleSummary ToSummary(string id, Dictionary<string, object> schedule) => new(
        id,
        schedule.TryGetValue("cron", out object? cron) ? cron?.ToString() ?? "" : "",
        schedule.TryGetValue("job", out object? job) ? job?.ToString() : null,
        schedule.TryGetValue("params", out object? @params) && @params is Dictionary<object, object> map
            ? map.ToDictionary(pair => pair.Key.ToString() ?? "", pair => pair.Value?.ToString() ?? "")
            : new Dictionary<string, string>(),
        IsTrue(schedule, "yes"),
        IsTrue(schedule, "rebuild"),
        !schedule.TryGetValue("enabled", out object? enabled) || IsTrueValue(enabled));

    private static bool IsTrue(Dictionary<string, object> schedule, string key)
        => schedule.TryGetValue(key, out object? value) && IsTrueValue(value);

    private static bool IsTrueValue(object? value)
        => value is bool boolValue ? boolValue : bool.TryParse(value?.ToString(), out bool parsed) && parsed;

    private static void EnsureBlockStyle(string sectionLine)
    {
        string rest = sectionLine[SectionKey.Length..].Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            throw new InvalidPipelineException(
                "schedules がインライン形式（schedules: {...}）のため自動編集できません。設定エディタで直接編集してください。");
        }
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

    private static (int Start, int End) FindEntryBlock(
        List<string> lines, int sectionStart, int sectionEnd, string id, string childIndent)
    {
        for (int index = sectionStart + 1; index < sectionEnd; index++)
        {
            if (!TryGetEntryId(lines[index], childIndent, out string? entryId) || entryId != id) continue;

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

    private static bool TryGetEntryId(string line, string childIndent, out string? id)
    {
        id = null;
        if (!line.StartsWith(childIndent, StringComparison.Ordinal)) return false;

        string rest = line[childIndent.Length..];
        if (rest.Length == 0 || char.IsWhiteSpace(rest[0]) || rest.StartsWith('#')) return false;

        int colon = rest.IndexOf(':');
        if (colon <= 0) return false;

        id = rest[..colon].Trim().Trim('"', '\'');
        return true;
    }

    private static int GetIndentLength(string line) => line.Length - line.TrimStart().Length;

    private static List<string> SplitLines(string yaml) => yaml.Replace("\r\n", "\n").Split('\n').ToList();

    /// <summary>schedules 配下だけを寛容に読み取るためのDTO。</summary>
    private sealed class RawRoot
    {
        public Dictionary<string, Dictionary<string, object>?> Schedules { get; set; } = new();
    }
}
