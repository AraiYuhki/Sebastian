using SebastianCi.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SebastianCi.Web;

/// <summary>ダッシュボード表示用のエージェントプール1件分の情報。Token は展開前の生値（例: $AGENT_TOKEN）。</summary>
public sealed record AgentSummary(string Url, string Token, List<string> Labels);

/// <summary>
/// 設定ファイル（YAMLテキスト）の agents セクションだけを行単位で編集する。
/// YAML全体を再シリアライズするとコメントや整形が失われるため、テキスト操作で行う。
/// </summary>
public static class AgentsConfigEditor
{
    private const string SectionKey = "agents:";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>agents セクションだけを寛容に読み出す（他のキーの不備には影響されない）。</summary>
    public static List<AgentSummary> Read(string yaml)
    {
        RawRoot root = Deserializer.Deserialize<RawRoot?>(yaml) ?? new RawRoot();
        return root.Agents
            .Select(agent => new AgentSummary(agent.Url, agent.Token, agent.Labels))
            .ToList();
    }

    /// <summary>エージェントのエントリを追加したYAMLを返す。</summary>
    public static string Add(string yaml, string url, string token, IReadOnlyList<string> labels)
    {
        List<string> entryLines = [$"  - url: {url}"];
        if (!string.IsNullOrWhiteSpace(token)) entryLines.Add($"    token: {token}");
        if (labels.Count > 0) entryLines.Add($"    labels: [{string.Join(", ", labels)}]");

        List<string> lines = SplitLines(yaml);
        int sectionIndex = lines.FindIndex(IsSectionHeader);
        if (sectionIndex >= 0)
        {
            EnsureBlockStyle(lines[sectionIndex]);
            lines.InsertRange(sectionIndex + 1, entryLines);
            return string.Join('\n', lines);
        }

        if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
        lines.Add(SectionKey);
        lines.AddRange(entryLines);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>url が一致するエントリを削除したYAMLを返す。エントリが無くなった場合はセクション行ごと取り除く。</summary>
    public static string Remove(string yaml, string url)
    {
        List<string> lines = SplitLines(yaml);
        (int start, int end) = FindSectionRange(lines);
        if (start < 0) return yaml;

        List<int> entryStarts = FindEntryStarts(lines, start, end);
        int target = FindEntryMatching(lines, entryStarts, end, url);
        if (target < 0) return yaml;

        int targetEnd = NextEntryStart(entryStarts, target, end);
        lines.RemoveRange(target, targetEnd - target);
        if (entryStarts.Count == 1) lines.RemoveAt(start);
        return string.Join('\n', lines);
    }

    private static void EnsureBlockStyle(string sectionLine)
    {
        string rest = sectionLine[SectionKey.Length..].Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            throw new InvalidPipelineException(
                "agents がインライン形式（agents: [...]）のため自動編集できません。設定エディタで直接編集してください。");
        }
    }

    private static List<string> SplitLines(string yaml) => yaml.Replace("\r\n", "\n").Split('\n').ToList();

    private static bool IsSectionHeader(string line) => line.StartsWith(SectionKey, StringComparison.Ordinal);

    private static (int Start, int End) FindSectionRange(List<string> lines)
    {
        int start = lines.FindIndex(IsSectionHeader);
        if (start < 0) return (-1, -1);

        for (int index = start + 1; index < lines.Count; index++)
        {
            if (IsTopLevelKey(lines[index])) return (start, index);
        }

        return (start, lines.Count);
    }

    private static bool IsTopLevelKey(string line)
        => line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#');

    private static List<int> FindEntryStarts(List<string> lines, int sectionStart, int sectionEnd)
    {
        List<int> starts = new();
        for (int index = sectionStart + 1; index < sectionEnd; index++)
        {
            if (lines[index].TrimStart().StartsWith("- ", StringComparison.Ordinal)) starts.Add(index);
        }

        return starts;
    }

    private static int FindEntryMatching(List<string> lines, List<int> entryStarts, int sectionEnd, string url)
    {
        foreach (int start in entryStarts)
        {
            int end = NextEntryStart(entryStarts, start, sectionEnd);
            if (lines.GetRange(start, end - start).Any(line => MatchesUrl(line, url))) return start;
        }

        return -1;
    }

    private static int NextEntryStart(List<int> entryStarts, int currentStart, int sectionEnd)
    {
        int next = entryStarts.FirstOrDefault(start => start > currentStart, -1);
        return next < 0 ? sectionEnd : next;
    }

    private static bool MatchesUrl(string line, string url)
    {
        string content = line.TrimStart().TrimStart('-').TrimStart();
        return content == $"url: {url}";
    }

    /// <summary>agents 配下だけを寛容に読み取るためのDTO。</summary>
    private sealed class RawRoot
    {
        public List<RawAgent> Agents { get; set; } = new();
    }

    private sealed class RawAgent
    {
        public string Url { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new();
    }
}
