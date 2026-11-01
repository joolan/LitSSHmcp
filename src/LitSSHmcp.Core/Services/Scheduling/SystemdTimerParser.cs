using System.Globalization;
using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Services.Scheduling;

public sealed class SystemdTimerInfo
{
    public string Unit { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ActivatedUnit { get; init; } = string.Empty;
    public string ActiveState { get; init; } = string.Empty;
    public string SubState { get; init; } = string.Empty;
    public string UnitFileState { get; init; } = string.Empty;
    public string FragmentPath { get; init; } = string.Empty;
    public DateTimeOffset? NextElapse { get; init; }
    public DateTimeOffset? LastTrigger { get; init; }
    public string NextElapseRaw { get; init; } = string.Empty;
    public string LastTriggerRaw { get; init; } = string.Empty;
    public IReadOnlyList<string> CalendarExpressions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<KeyValuePair<string, string>> MonotonicValues { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();
    public string ServiceCommand { get; init; } = string.Empty;
    public string ServiceUser { get; init; } = string.Empty;
    public string ServiceWorkingDirectory { get; init; } = string.Empty;
    public string ServiceFragmentPath { get; init; } = string.Empty;

    public bool IsActive => string.Equals(ActiveState, "active", StringComparison.OrdinalIgnoreCase);

    public string ScheduleDescription
    {
        get
        {
            var descriptions = new List<string>();
            descriptions.AddRange(CalendarExpressions.Select(SystemdTimerParser.DescribeCalendar));
            descriptions.AddRange(MonotonicValues.Select(x => SystemdTimerParser.DescribeMonotonic(x.Key, x.Value)));
            descriptions.RemoveAll(string.IsNullOrWhiteSpace);
            return descriptions.Count == 0 ? "未配置触发时间" : string.Join("；", descriptions.Distinct());
        }
    }

    public string StateText
    {
        get
        {
            var enabled = UnitFileState is "enabled" or "enabled-runtime";
            if (string.Equals(ActiveState, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return "异常";
            }

            if (string.Equals(UnitFileState, "masked", StringComparison.OrdinalIgnoreCase))
            {
                return "已屏蔽";
            }

            if (IsActive)
            {
                return enabled ? "运行中" : "运行中（开机未启用）";
            }

            if (string.Equals(UnitFileState, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                return "已暂停";
            }

            return enabled ? "已停止（开机启用）" : "已停止";
        }
    }
}

public static class SystemdTimerParser
{
    private static readonly Regex CalendarRegex = new(@"OnCalendar\s*=\s*([^;}]+)", RegexOptions.Compiled);
    private static readonly Regex MonotonicRegex = new(@"(OnBootSec|OnStartupSec|OnUnitActiveSec|OnUnitInactiveSec|OnActiveSec)\s*=\s*([^;}]+)", RegexOptions.Compiled);
    private static readonly Regex ExecStartRegex = new(@"argv\[\]\s*=\s*([^;]+)", RegexOptions.Compiled);

    public static SystemdTimerInfo Parse(string unit, string timerProperties, string? serviceProperties)
    {
        var timer = ParseProperties(timerProperties);
        var service = ParseProperties(serviceProperties ?? string.Empty);

        var calendar = new List<string>();
        if (timer.TryGetValue("TimersCalendar", out var calendarRaw))
        {
            foreach (Match match in CalendarRegex.Matches(calendarRaw))
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length > 0)
                {
                    calendar.Add(value);
                }
            }
        }

        var monotonic = new List<KeyValuePair<string, string>>();
        if (timer.TryGetValue("TimersMonotonic", out var monotonicRaw))
        {
            foreach (Match match in MonotonicRegex.Matches(monotonicRaw))
            {
                monotonic.Add(new KeyValuePair<string, string>(
                    match.Groups[1].Value.Trim(),
                    match.Groups[2].Value.Trim()));
            }
        }

        var commands = new List<string>();
        foreach (var pair in service.Where(x => x.Key == "ExecStart"))
        {
            foreach (Match match in ExecStartRegex.Matches(pair.Value))
            {
                var command = match.Groups[1].Value.Trim();
                if (command.Length > 0)
                {
                    commands.Add(command);
                }
            }
        }

        return new SystemdTimerInfo
        {
            Unit = Get(timer, "Id") is { Length: > 0 } id ? id : unit,
            Description = Get(timer, "Description"),
            ActivatedUnit = Get(timer, "Unit"),
            ActiveState = Get(timer, "ActiveState"),
            SubState = Get(timer, "SubState"),
            UnitFileState = Get(timer, "UnitFileState"),
            FragmentPath = Get(timer, "FragmentPath"),
            NextElapse = ParseTimestamp(Get(timer, "NextElapseUSecRealtime")),
            LastTrigger = ParseTimestamp(Get(timer, "LastTriggerUSec")),
            NextElapseRaw = Get(timer, "NextElapseUSecRealtime"),
            LastTriggerRaw = Get(timer, "LastTriggerUSec"),
            CalendarExpressions = calendar,
            MonotonicValues = monotonic,
            ServiceCommand = string.Join("\n", commands.Distinct()),
            ServiceUser = Get(service, "User") is { Length: > 0 } user ? user : "root",
            ServiceWorkingDirectory = Get(service, "WorkingDirectory"),
            ServiceFragmentPath = Get(service, "FragmentPath")
        };
    }

    public static string DescribeCalendar(string expression)
    {
        var text = expression.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var lower = text.ToLowerInvariant();
        switch (lower)
        {
            case "minutely":
                return "每分钟";
            case "hourly":
                return "每小时整点";
            case "daily":
                return "每天 00:00";
            case "weekly":
                return "每周一 00:00";
            case "monthly":
                return "每月 1 日 00:00";
            case "quarterly":
                return "每季度";
            case "semi-annually":
                return "每半年";
            case "yearly":
            case "annually":
                return "每年 1 月 1 日 00:00";
        }

        var minuteStep = Regex.Match(text, @"^\*:(\d{1,2})/(\d{1,2})(?::\d{1,2})?$");
        if (minuteStep.Success)
        {
            return $"每 {minuteStep.Groups[2].Value} 分钟";
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return text;
        }

        string weekday;
        string datePart;
        string timePart;
        if (parts.Length >= 2 && IsWeekdaySpec(parts[0]))
        {
            weekday = DescribeWeekdaySpec(parts[0]);
            if (parts.Length >= 3)
            {
                datePart = parts[1];
                timePart = parts[2];
            }
            else
            {
                datePart = "*-*-*";
                timePart = parts[1];
            }
        }
        else
        {
            weekday = string.Empty;
            datePart = parts[0];
            timePart = parts.Length > 1 ? parts[1] : string.Empty;
        }

        var dateText = DescribeDate(datePart);
        var timeText = DescribeTime(timePart);

        if (dateText.Length == 0 && timeText.Length == 0)
        {
            return text;
        }

        if (weekday.Length > 0)
        {
            return $"{weekday} {(dateText == "每天" ? timeText : dateText + " " + timeText)}".Trim();
        }

        if (dateText == "每天" && timeText.StartsWith('每'))
        {
            return timeText;
        }

        return $"{dateText} {timeText}".Trim();
    }

    public static string DescribeMonotonic(string key, string value)
    {
        var span = DescribeSpan(value);
        return key switch
        {
            "OnBootSec" => $"系统启动 {span}后",
            "OnStartupSec" => $"systemd 启动 {span}后",
            "OnActiveSec" => $"定时器启动 {span}后",
            "OnUnitActiveSec" => $"距上次运行 {span}后",
            "OnUnitInactiveSec" => $"距上次停止 {span}后",
            _ => $"{key} {span}"
        };
    }

    private static bool IsWeekdaySpec(string value)
        => Regex.IsMatch(value, @"^[A-Za-z]{3}(?:(?:\.\.)[A-Za-z]{3}|(?:,[A-Za-z]{3})*)$");

    private static string DescribeWeekdaySpec(string tokens)
    {
        if (tokens.Contains("..", StringComparison.Ordinal))
        {
            var pair = tokens.Split("..", StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length == 2)
            {
                var start = WeekdayName(pair[0]);
                var end = WeekdayName(pair[1]);
                return start == "一" && end == "五" ? "工作日" : $"周{start}至周{end}";
            }
        }

        var days = tokens.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(WeekdayName)
            .Distinct()
            .ToArray();
        if (days.SequenceEqual(new[] { "一", "二", "三", "四", "五" }))
        {
            return "工作日";
        }

        return "每周" + string.Join("、", days);
    }

    private static string DescribeDate(string datePart)
    {
        if (string.IsNullOrWhiteSpace(datePart) || datePart == "*-*-*")
        {
            return "每天";
        }

        var parts = datePart.Split('-');
        if (parts.Length != 3)
        {
            return datePart;
        }

        var year = parts[0];
        var month = parts[1];
        var day = parts[2];

        if (year != "*" && year.Length > 0 && month != "*" && day != "*")
        {
            return $"{year} 年 {TrimZero(month)} 月 {TrimZero(day)} 日";
        }

        if (month != "*" && day != "*")
        {
            return $"每年 {TrimZero(month)} 月 {TrimZero(day)} 日";
        }

        if (month != "*" && day == "*")
        {
            return $"每年 {TrimZero(month)} 月";
        }

        if (day != "*")
        {
            return $"每月 {TrimZero(day)} 日";
        }

        return "每天";
    }

    private static string DescribeTime(string timePart)
    {
        if (string.IsNullOrWhiteSpace(timePart) || timePart == "*:*:*" || timePart == "*:*")
        {
            return "每分钟";
        }

        var step = Regex.Match(timePart, @"^(?:\*|\d{1,2}):(\d{1,2})/(\d{1,2})(?::\d{1,2})?$");
        if (step.Success)
        {
            var stepValue = step.Groups[2].Value;
            return $"每 {stepValue} 分钟";
        }

        var match = Regex.Match(timePart, @"^(\*|\d{1,2}):(\*|\d{1,2})(?::(\d{1,2}))?$");
        if (!match.Success)
        {
            return timePart;
        }

        var hour = match.Groups[1].Value;
        var minute = match.Groups[2].Value;
        var second = match.Groups[3].Value;

        if (hour == "*" && minute == "*")
        {
            return "每分钟";
        }

        if (hour == "*" && int.TryParse(minute, out var minuteValue))
        {
            return $"每小时第 {minuteValue} 分钟";
        }

        if (int.TryParse(hour, out var hourValue) && int.TryParse(minute, out var minuteNumber))
        {
            return second.Length > 0 && int.TryParse(second, out var secondValue) && secondValue != 0
                ? $"{hourValue:00}:{minuteNumber:00}:{secondValue:00}"
                : $"{hourValue:00}:{minuteNumber:00}";
        }

        return timePart;
    }

    private static string DescribeSpan(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return value;
        }

        var parts = Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*(us|ms|s|sec|secs|second|seconds|m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days|w|week|weeks|month|months|y|year|years)")
            .Select(m => (Value: m.Groups[1].Value, Unit: m.Groups[2].Value.ToLowerInvariant()))
            .ToArray();
        if (parts.Length == 0)
        {
            return text;
        }

        return string.Join(" ", parts.Select(p => $"{p.Value} {UnitName(p.Unit)}"));
    }

    private static string UnitName(string unit) => unit switch
    {
        "us" => "微秒",
        "ms" => "毫秒",
        "s" or "sec" or "secs" or "second" or "seconds" => "秒",
        "m" or "min" or "mins" or "minute" or "minutes" => "分钟",
        "h" or "hr" or "hrs" or "hour" or "hours" => "小时",
        "d" or "day" or "days" => "天",
        "w" or "week" or "weeks" => "周",
        "month" or "months" => "个月",
        "y" or "year" or "years" => "年",
        _ => unit
    };

    private static string WeekdayName(string token) => token.ToUpperInvariant() switch
    {
        "SUN" => "日",
        "MON" => "一",
        "TUE" => "二",
        "WED" => "三",
        "THU" => "四",
        "FRI" => "五",
        "SAT" => "六",
        _ => token
    };

    private static string TrimZero(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : value;
    }

    private static DateTimeOffset? ParseTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "n/a" or "-")
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) &&
            microseconds > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseProperties(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..];
            if (result.TryGetValue(key, out var existing))
            {
                result[key] = existing + "\n" + value;
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string Get(Dictionary<string, string> properties, string key)
        => properties.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
}
