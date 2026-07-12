using SebastianCi.Core;

namespace SebastianCi.Web;

/// <summary>
/// 設定ファイル（YAMLテキスト）の plugins セクションだけを行単位で編集する。
/// YAML全体を再シリアライズするとコメントや整形が失われるため、テキスト操作で行う。
/// </summary>
public static class PluginConfigEditor
{
    private const string SectionKey = "plugins:";

    /// <summary>NuGet パッケージのエントリを追加したYAMLを返す。</summary>
    public static string AddPackage(string yaml, string package, string version)
    {
        List<string> entryLines = [$"  - package: {package}"];
        if (!string.IsNullOrWhiteSpace(version)) entryLines.Add($"    version: {version}");
        return AddEntry(yaml, entryLines);
    }

    /// <summary>ローカルアセンブリのエントリを追加したYAMLを返す。</summary>
    public static string AddPath(string yaml, string path) => AddEntry(yaml, [$"  - path: {path}"]);

    /// <summary>
    /// package または path が identifier に一致するエントリを削除したYAMLを返す。
    /// エントリが無くなった場合は plugins: の行ごと取り除く。
    /// </summary>
    public static string Remove(string yaml, string identifier)
    {
        List<string> lines = SplitLines(yaml);
        (int start, int end) = FindSectionRange(lines);
        if (start < 0) return yaml;

        List<int> entryStarts = FindEntryStarts(lines, start, end);
        int target = FindEntryMatching(lines, entryStarts, end, identifier);
        if (target < 0) return yaml;

        int targetEnd = NextEntryStart(entryStarts, target, end);
        lines.RemoveRange(target, targetEnd - target);
        if (entryStarts.Count == 1) lines.RemoveAt(start);
        return string.Join('\n', lines);
    }

    private static string AddEntry(string yaml, List<string> entryLines)
    {
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

    private static void EnsureBlockStyle(string sectionLine)
    {
        string rest = sectionLine[SectionKey.Length..].Trim();
        if (rest.Length > 0 && !rest.StartsWith('#'))
        {
            throw new PluginException(
                "plugins がインライン形式（plugins: [...]）のため自動編集できません。設定エディタで直接編集してください。");
        }
    }

    private static List<string> SplitLines(string yaml) => yaml.Replace("\r\n", "\n").Split('\n').ToList();

    private static bool IsSectionHeader(string line) => line.StartsWith(SectionKey, StringComparison.Ordinal);

    /// <summary>plugins セクションの範囲（ヘッダー行の位置と、次のトップレベルキーの位置）を返す。</summary>
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

    private static int FindEntryMatching(List<string> lines, List<int> entryStarts, int sectionEnd, string identifier)
    {
        foreach (int start in entryStarts)
        {
            int end = NextEntryStart(entryStarts, start, sectionEnd);
            if (lines.GetRange(start, end - start).Any(line => MatchesIdentifier(line, identifier))) return start;
        }

        return -1;
    }

    private static int NextEntryStart(List<int> entryStarts, int currentStart, int sectionEnd)
    {
        int next = entryStarts.FirstOrDefault(start => start > currentStart, -1);
        return next < 0 ? sectionEnd : next;
    }

    private static bool MatchesIdentifier(string line, string identifier)
    {
        string content = line.TrimStart().TrimStart('-').TrimStart();
        return content == $"package: {identifier}" || content == $"path: {identifier}";
    }
}
