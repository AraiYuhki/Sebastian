using SebastianCi.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SebastianCi.Web;

/// <summary>ダッシュボード表示用の実行時パラメーター1件分の情報。Default が null なら指定必須。</summary>
public sealed record ParamSummary(string Name, string? Default, string Description, List<string> Choices);

/// <summary>ダッシュボードのフォームから送られてくるパラメーター1件分の入力。</summary>
public sealed record ParamFormPayload(
    string? OriginalName, string Name, bool Required, string? Default, string? Description, List<string>? Choices);

/// <summary>
/// 設定ファイル（YAMLテキスト）の params セクションをパラメーター単位で編集する。
/// 対象ブロックだけを差し替え、コメントや他のセクションの行はそのまま保持する。
/// </summary>
public static class ParamsConfigEditor
{
    private const string SectionKey = "params:";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithIndentedSequences()
        .Build();

    /// <summary>params セクションだけを寛容に読み出す（他のキーの不備には影響されない）。</summary>
    public static List<ParamSummary> Read(string yaml)
    {
        RawRoot root = Deserializer.Deserialize<RawRoot?>(yaml) ?? new RawRoot();
        return root.Params.Select(pair => ToSummary(pair.Key, pair.Value ?? new())).ToList();
    }

    /// <summary>パラメーターを追加または更新したYAMLを返す。</summary>
    public static string Upsert(string yaml, ParamFormPayload payload)
    {
        string originalName = string.IsNullOrWhiteSpace(payload.OriginalName) ? payload.Name : payload.OriginalName!;
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return AppendNewSection(lines, BuildBlock(payload, "  "));

        EnsureBlockStyle(lines[sectionStart]);
        string childIndent = DetectChildIndent(lines, sectionStart, sectionEnd);
        (int blockStart, int blockEnd) = FindEntryBlock(lines, sectionStart, sectionEnd, originalName, childIndent);
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

    /// <summary>指定した名前のパラメーターを削除したYAMLを返す。最後の1件を消すとセクション行ごと取り除く。</summary>
    public static string Remove(string yaml, string name)
    {
        List<string> lines = SplitLines(yaml);
        (int sectionStart, int sectionEnd) = FindSectionRange(lines);
        if (sectionStart < 0) return yaml;

        EnsureBlockStyle(lines[sectionStart]);
        string childIndent = DetectChildIndent(lines, sectionStart, sectionEnd);
        (int blockStart, int blockEnd) = FindEntryBlock(lines, sectionStart, sectionEnd, name, childIndent);
        if (blockStart < 0) return yaml;

        bool wasLastEntry = Read(string.Join('\n', lines)).Count == 1;
        lines.RemoveRange(blockStart, blockEnd - blockStart);
        if (wasLastEntry) lines.RemoveAt(sectionStart);
        return string.Join('\n', lines);
    }

    private static List<string> BuildBlock(ParamFormPayload payload, string childIndent)
    {
        Dictionary<string, object> map = new();
        if (!payload.Required) map["default"] = payload.Default ?? "";
        if (!string.IsNullOrWhiteSpace(payload.Description)) map["description"] = payload.Description.Trim();
        if (payload.Choices is { Count: > 0 }) map["choices"] = payload.Choices;

        string contentIndent = childIndent + "  ";
        List<string> block = [$"{childIndent}{payload.Name}:"];
        if (map.Count == 0)
        {
            // すべて省略（default 無し = 必須のみ）の場合も有効なマップになるよう {} を添える
            block[0] = $"{childIndent}{payload.Name}: {{}}";
            return block;
        }

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

    private static ParamSummary ToSummary(string name, Dictionary<string, object> parameter) => new(
        name,
        parameter.TryGetValue("default", out object? value) ? value?.ToString() ?? "" : null,
        parameter.TryGetValue("description", out object? description) ? description?.ToString() ?? "" : "",
        parameter.TryGetValue("choices", out object? choices) && choices is List<object> list
            ? list.Select(item => item?.ToString() ?? "").ToList()
            : new List<string>());

    private static void EnsureBlockStyle(string sectionLine)
    {
        string rest = sectionLine[SectionKey.Length..].Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            throw new InvalidPipelineException(
                "params がインライン形式（params: {...}）のため自動編集できません。設定エディタで直接編集してください。");
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
        List<string> lines, int sectionStart, int sectionEnd, string name, string childIndent)
    {
        for (int index = sectionStart + 1; index < sectionEnd; index++)
        {
            if (!TryGetEntryName(lines[index], childIndent, out string? entryName) || entryName != name) continue;

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

    private static bool TryGetEntryName(string line, string childIndent, out string? name)
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

    /// <summary>params 配下だけを寛容に読み取るためのDTO。</summary>
    private sealed class RawRoot
    {
        public Dictionary<string, Dictionary<string, object>?> Params { get; set; } = new();
    }
}
