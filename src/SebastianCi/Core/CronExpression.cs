namespace SebastianCi.Core;

/// <summary>
/// Jenkins の cron トリガーと互換の、標準的な5フィールドcron式（分 時 日 月 曜日）を解釈する。
/// 外部ライブラリに依存せず、ダッシュボードのスケジュール実行機能で使う範囲だけを実装する
/// （対応: *, カンマ区切り, 範囲 a-b, ステップ */n・a-b/n。名前指定（JAN・MON等）や L/W/# は非対応）。
/// </summary>
public sealed class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;

    private CronExpression(
        HashSet<int> minutes, HashSet<int> hours, HashSet<int> daysOfMonth,
        HashSet<int> months, HashSet<int> daysOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    /// <summary>cron式を解釈する。失敗時は error に日本語の理由を入れて false を返す。</summary>
    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        string[] fields = (expression ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "cron式は「分 時 日 月 曜日」の5項目で指定してください（例: 0 3 * * *）。";
            return false;
        }

        try
        {
            HashSet<int> minutes = ParseField(fields[0], 0, 59);
            HashSet<int> hours = ParseField(fields[1], 0, 23);
            HashSet<int> daysOfMonth = ParseField(fields[2], 1, 31);
            HashSet<int> months = ParseField(fields[3], 1, 12);
            HashSet<int> daysOfWeek = ParseField(fields[4], 0, 7)
                .Select(value => value == 7 ? 0 : value)
                .ToHashSet();
            cron = new CronExpression(minutes, hours, daysOfMonth, months, daysOfWeek);
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>指定した日時（分単位で判定、秒は無視）が実行対象かどうか。</summary>
    public bool Matches(DateTime moment)
        => _minutes.Contains(moment.Minute)
           && _hours.Contains(moment.Hour)
           && _daysOfMonth.Contains(moment.Day)
           && _months.Contains(moment.Month)
           && _daysOfWeek.Contains((int)moment.DayOfWeek);

    /// <summary>
    /// 指定日時より後（同時刻は含まない）で最初に条件を満たす分刻みの日時を返す。
    /// ダッシュボード上の「次回実行予定」表示専用で、最大2年先まで探索して見つからなければ null を返す。
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime afterExclusive)
    {
        DateTime candidate = new DateTime(
            afterExclusive.Year, afterExclusive.Month, afterExclusive.Day,
            afterExclusive.Hour, afterExclusive.Minute, 0, afterExclusive.Kind).AddMinutes(1);
        DateTime limit = afterExclusive.AddYears(2);
        while (candidate < limit)
        {
            if (Matches(candidate)) return candidate;
            candidate = candidate.AddMinutes(1);
        }

        return null;
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        HashSet<int> values = new();
        foreach (string part in field.Split(','))
        {
            ParsePart(part, min, max, values);
        }

        return values;
    }

    private static void ParsePart(string part, int min, int max, HashSet<int> values)
    {
        string rangePart = part;
        int step = 1;
        int slash = part.IndexOf('/');
        if (slash >= 0)
        {
            rangePart = part[..slash];
            if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
            {
                throw new FormatException($"cron式のステップ指定「{part}」が不正です。");
            }
        }

        int rangeStart, rangeEnd;
        if (rangePart == "*")
        {
            rangeStart = min;
            rangeEnd = max;
        }
        else if (rangePart.Contains('-'))
        {
            string[] bounds = rangePart.Split('-');
            if (bounds.Length != 2 || !int.TryParse(bounds[0], out rangeStart) || !int.TryParse(bounds[1], out rangeEnd))
            {
                throw new FormatException($"cron式の範囲指定「{part}」が不正です。");
            }
        }
        else
        {
            if (!int.TryParse(rangePart, out rangeStart))
            {
                throw new FormatException($"cron式の値「{part}」が不正です。");
            }

            rangeEnd = rangeStart;
        }

        if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd)
        {
            throw new FormatException($"cron式の値「{part}」は{min}〜{max}の範囲で指定してください。");
        }

        for (int value = rangeStart; value <= rangeEnd; value += step)
        {
            values.Add(value);
        }
    }
}
