using System.Globalization;

namespace LitSSHmcp.Core.Services.Scheduling;

public sealed class CronSchedule
{
    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1,
        ["FEB"] = 2,
        ["MAR"] = 3,
        ["APR"] = 4,
        ["MAY"] = 5,
        ["JUN"] = 6,
        ["JUL"] = 7,
        ["AUG"] = 8,
        ["SEP"] = 9,
        ["OCT"] = 10,
        ["NOV"] = 11,
        ["DEC"] = 12
    };

    private static readonly Dictionary<string, int> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 0,
        ["MON"] = 1,
        ["TUE"] = 2,
        ["WED"] = 3,
        ["THU"] = 4,
        ["FRI"] = 5,
        ["SAT"] = 6
    };

    private readonly SortedSet<int> _minutes;
    private readonly SortedSet<int> _hours;
    private readonly SortedSet<int> _daysOfMonth;
    private readonly SortedSet<int> _months;
    private readonly SortedSet<int> _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;
    private readonly bool _monthRestricted;

    private CronSchedule(
        SortedSet<int> minutes,
        SortedSet<int> hours,
        SortedSet<int> daysOfMonth,
        SortedSet<int> months,
        SortedSet<int> daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted,
        bool monthRestricted,
        bool isReboot)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
        _monthRestricted = monthRestricted;
        IsReboot = isReboot;
    }

    public bool IsReboot { get; }

    public IReadOnlySet<int> Minutes => _minutes;
    public IReadOnlySet<int> Hours => _hours;
    public IReadOnlySet<int> DaysOfMonth => _daysOfMonth;
    public IReadOnlySet<int> Months => _months;
    public IReadOnlySet<int> DaysOfWeek => _daysOfWeek;

    public static bool TryParse(string? expression, out CronSchedule? schedule, out string error)
    {
        schedule = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式为空";
            return false;
        }

        var text = expression.Trim();
        if (text.StartsWith('@'))
        {
            switch (text.ToLowerInvariant())
            {
                case "@reboot":
                    schedule = new CronSchedule(
                        new SortedSet<int>(), new SortedSet<int>(), new SortedSet<int>(),
                        new SortedSet<int>(), new SortedSet<int>(), false, false, false, true);
                    return true;
                case "@yearly":
                case "@annually":
                    text = "0 0 1 1 *";
                    break;
                case "@monthly":
                    text = "0 0 1 * *";
                    break;
                case "@weekly":
                    text = "0 0 * * 0";
                    break;
                case "@daily":
                case "@midnight":
                    text = "0 0 * * *";
                    break;
                case "@hourly":
                    text = "0 * * * *";
                    break;
                default:
                    error = $"不支持的宏：{expression.Trim()}";
                    return false;
            }
        }

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "需要 5 个字段（分 时 日 月 周）";
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, null, out var minutes, out error) ||
            !TryParseField(fields[1], 0, 23, null, out var hours, out error) ||
            !TryParseField(fields[2], 1, 31, null, out var daysOfMonth, out error) ||
            !TryParseField(fields[3], 1, 12, MonthNames, out var months, out error) ||
            !TryParseField(fields[4], 0, 7, DayNames, out var daysOfWeek, out error))
        {
            return false;
        }

        if (daysOfWeek.Remove(7))
        {
            daysOfWeek.Add(0);
        }

        schedule = new CronSchedule(
            minutes,
            hours,
            daysOfMonth,
            months,
            daysOfWeek,
            fields[2].Trim() != "*",
            fields[4].Trim() != "*",
            fields[3].Trim() != "*",
            false);
        return true;
    }

    public string Describe()
    {
        if (IsReboot)
        {
            return "系统启动时";
        }

        var timePart = DescribeTime();
        var datePart = DescribeDate();
        var description = datePart.Length == 0
            ? (timePart.StartsWith('每') ? timePart : "每天 " + timePart)
            : datePart + (timePart.StartsWith('每') ? timePart : " " + timePart);

        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
        {
            description += "（cron 规则：日或星期满足其一）";
        }

        return description;
    }

    public IReadOnlyList<DateTime> GetNextOccurrences(DateTime fromLocal, int count)
    {
        var results = new List<DateTime>();
        if (IsReboot || count <= 0)
        {
            return results;
        }

        var minutes = _minutes.ToArray();
        var hours = _hours.ToArray();
        var start = new DateTime(fromLocal.Year, fromLocal.Month, fromLocal.Day,
            fromLocal.Hour, fromLocal.Minute, 0, fromLocal.Kind);
        var limit = start.AddYears(5);

        for (var day = start.Date; day <= limit && results.Count < count; day = day.AddDays(1))
        {
            if (!_months.Contains(day.Month))
            {
                continue;
            }

            var dayOfMonthMatch = _daysOfMonth.Contains(day.Day);
            var dayOfWeekMatch = _daysOfWeek.Contains((int)day.DayOfWeek);
            var dateMatch = (_dayOfMonthRestricted, _dayOfWeekRestricted) switch
            {
                (true, true) => dayOfMonthMatch || dayOfWeekMatch,
                (true, false) => dayOfMonthMatch,
                (false, true) => dayOfWeekMatch,
                _ => true
            };
            if (!dateMatch)
            {
                continue;
            }

            foreach (var hour in hours)
            {
                foreach (var minute in minutes)
                {
                    var candidate = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, fromLocal.Kind);
                    if (candidate > fromLocal)
                    {
                        results.Add(candidate);
                        if (results.Count >= count)
                        {
                            return results;
                        }
                    }
                }
            }
        }

        return results;
    }

    private string DescribeTime()
    {
        var minutes = _minutes.ToArray();
        var hours = _hours.ToArray();
        var allMinutes = minutes.Length == 60;
        var allHours = hours.Length == 24;

        if (TryDescribeInterval(minutes, 60, out var minuteInterval))
        {
            if (allHours)
            {
                return minuteInterval == 1 ? "每分钟" : $"每 {minuteInterval} 分钟";
            }

            if (hours.Length == 1)
            {
                return $"{hours[0]:00} 点内每 {minuteInterval} 分钟";
            }

            return $"{FormatNumbers(hours, 2)} 点的每 {minuteInterval} 分钟";
        }

        if (minutes.Length == 1 && minutes[0] == 0)
        {
            if (allHours)
            {
                return "每小时整点";
            }

            if (TryDescribeInterval(hours, 24, out var hourInterval))
            {
                return hourInterval == 1 ? "每小时整点" : $"每 {hourInterval} 小时";
            }
        }

        if (allMinutes)
        {
            if (allHours)
            {
                return "每分钟";
            }

            if (hours.Length == 1)
            {
                return $"{hours[0]:00} 点内每分钟";
            }

            return $"{FormatNumbers(hours, 2)} 点的每分钟";
        }

        if (allHours)
        {
            return minutes.Length == 1
                ? minutes[0] == 0 ? "每小时整点" : $"每小时第 {minutes[0]} 分钟"
                : $"每小时的第 {FormatNumbers(minutes, 2)} 分钟";
        }

        if (hours.Length == 1)
        {
            return minutes.Length == 1
                ? $"{hours[0]:00}:{minutes[0]:00}"
                : $"{hours[0]:00} 的第 {FormatNumbers(minutes, 2)} 分钟";
        }

        if (minutes.Length == 1)
        {
            return $"{FormatNumbers(hours, 2)} 点的 {minutes[0]:00} 分";
        }

        var times = new List<string>();
        foreach (var hour in hours)
        {
            foreach (var minute in minutes)
            {
                times.Add($"{hour:00}:{minute:00}");
                if (times.Count >= 6)
                {
                    break;
                }
            }

            if (times.Count >= 6)
            {
                break;
            }
        }

        var suffix = hours.Length * minutes.Length > times.Count ? " 等" : string.Empty;
        return string.Join("、", times) + suffix;
    }

    private string DescribeDate()
    {
        var hasDayOfMonth = _dayOfMonthRestricted;
        var hasDayOfWeek = _dayOfWeekRestricted;
        var hasMonth = _monthRestricted;

        if (!hasDayOfMonth && !hasDayOfWeek && !hasMonth)
        {
            return string.Empty;
        }

        if (hasDayOfWeek && !hasDayOfMonth)
        {
            var weekdays = DescribeWeekdays();
            return hasMonth ? $"每年 {DescribeMonths()} 月的{weekdays}" : weekdays;
        }

        if (hasDayOfMonth && !hasDayOfWeek)
        {
            return hasMonth
                ? $"每年 {DescribeMonths()} 月的 {DescribeDaysOfMonth()} 日"
                : $"每月 {DescribeDaysOfMonth()} 日";
        }

        if (hasDayOfMonth)
        {
            var both = $"每月 {DescribeDaysOfMonth()} 日或{DescribeWeekdays()}";
            return hasMonth
                ? $"每年 {DescribeMonths()} 月的 {DescribeDaysOfMonth()} 日或{DescribeWeekdays()}"
                : both;
        }

        return $"每年 {DescribeMonths()} 月的每天";
    }

    private string DescribeWeekdays()
    {
        var days = _daysOfWeek.OrderBy(x => x).ToArray();
        if (days.Length == 7)
        {
            return "每天";
        }

        if (days.SequenceEqual(new[] { 1, 2, 3, 4, 5 }))
        {
            return "工作日";
        }

        if (days.SequenceEqual(new[] { 0, 6 }))
        {
            return "周末";
        }

        if (days.Length > 1 && IsContiguous(days))
        {
            return $"周{WeekdayName(days[0])}至周{WeekdayName(days[^1])}";
        }

        if (days.Length == 1)
        {
            return $"周{WeekdayName(days[0])}";
        }

        return "每周" + string.Join("、", days.Select(WeekdayName));
    }

    private string DescribeMonths()
    {
        var months = _months.OrderBy(x => x).ToArray();
        return FormatNumbers(months, 0);
    }

    private string DescribeDaysOfMonth()
    {
        return FormatNumbers(_daysOfMonth.OrderBy(x => x).ToArray(), 0);
    }

    private static string FormatNumbers(int[] values, int pad)
    {
        var shown = values.Take(6)
            .Select(v => pad > 0 ? v.ToString("D" + pad, CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var text = string.Join("、", shown);
        return values.Length > shown.Length ? $"{text} 等 {values.Length} 项" : text;
    }

    private static string WeekdayName(int value) => value switch
    {
        0 => "日",
        1 => "一",
        2 => "二",
        3 => "三",
        4 => "四",
        5 => "五",
        6 => "六",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static bool IsContiguous(int[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] != values[i - 1] + 1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDescribeInterval(int[] values, int range, out int interval)
    {
        interval = 0;
        if (values.Length < 2 || values[0] != 0)
        {
            return false;
        }

        interval = values[1] - values[0];
        if (interval <= 0)
        {
            return false;
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] != i * interval)
            {
                return false;
            }
        }

        return values[^1] + interval >= range;
    }

    private static bool TryParseField(
        string text,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? names,
        out SortedSet<int> values,
        out string error)
    {
        values = new SortedSet<int>();
        error = string.Empty;

        foreach (var rawPart in text.Split(','))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                error = $"字段“{text}”包含空项";
                return false;
            }

            var slash = part.IndexOf('/');
            var rangeText = slash >= 0 ? part[..slash] : part;
            var step = 1;
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    error = $"步长无效：{part}";
                    return false;
                }
            }

            int start;
            int end;
            if (rangeText == "*")
            {
                start = min;
                end = max;
            }
            else
            {
                var dash = rangeText.IndexOf('-', 1);
                if (dash > 0)
                {
                    if (!TryParseValue(rangeText[..dash], min, max, names, out start) ||
                        !TryParseValue(rangeText[(dash + 1)..], min, max, names, out end))
                    {
                        error = $"取值无效：{rangeText}";
                        return false;
                    }
                }
                else
                {
                    if (!TryParseValue(rangeText, min, max, names, out start))
                    {
                        error = $"取值无效：{rangeText}";
                        return false;
                    }

                    end = slash >= 0 ? max : start;
                }
            }

            if (start > end)
            {
                error = $"范围起点大于终点：{rangeText}";
                return false;
            }

            for (var value = start; value <= end; value += step)
            {
                values.Add(value);
            }
        }

        return true;
    }

    private static bool TryParseValue(
        string token,
        int min,
        int max,
        IReadOnlyDictionary<string, int>? names,
        out int value)
    {
        token = token.Trim();
        if (names is not null && names.TryGetValue(token, out value))
        {
            return true;
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value >= min &&
               value <= max;
    }
}
