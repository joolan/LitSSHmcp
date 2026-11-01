namespace LitSSHmcp.Core.Services.Scheduling;

public static class ScheduledTasksProbeCommands
{
    public const string Direct = """
LC_ALL=C
echo __LITSSH_META__
printf 'uname=%s\n' "$(uname -s 2>/dev/null)"
printf 'hostname=%s\n' "$(hostname -f 2>/dev/null || hostname 2>/dev/null)"
printf 'user=%s\n' "$(id -un 2>/dev/null)"
printf 'uid=%s\n' "$(id -u 2>/dev/null)"
printf 'date=%s\n' "$(date '+%Y-%m-%dT%H:%M:%S%z' 2>/dev/null)"
printf 'os=%s\n' "$( (. /etc/os-release 2>/dev/null; printf '%s' "$PRETTY_NAME") 2>/dev/null)"
printf 'kernel=%s\n' "$(uname -r 2>/dev/null)"
printf 'crontab=%s\n' "$(command -v crontab 2>/dev/null)"
printf 'systemctl=%s\n' "$(command -v systemctl 2>/dev/null)"
printf 'cron_state=%s\n' "$( (systemctl is-active cron 2>/dev/null || systemctl is-active crond 2>/dev/null) | head -n 1)"
echo __LITSSH_CURRENT_CRONTAB__
crontab -l 2>&1
echo __LITSSH_ETC_CRONTAB__
cat /etc/crontab 2>/dev/null
echo __LITSSH_CRON_D__
for f in /etc/cron.d/*; do
  [ -f "$f" ] || continue
  echo "###FILE:$f"
  cat "$f" 2>/dev/null
done
echo __LITSSH_CRON_DIRS__
for d in hourly daily weekly monthly; do
  for f in /etc/cron.$d/*; do
    [ -f "$f" ] || continue
    echo "###FILE:$f|$(stat -c '%a|%U|%G|%s|%Y' "$f" 2>/dev/null)"
  done
done
echo __LITSSH_TIMERS__
if command -v systemctl >/dev/null 2>&1; then
  for u in $(systemctl list-unit-files --type=timer --no-legend --no-pager 2>/dev/null | awk '$1 ~ /\.timer$/ && $1 !~ /@/ {print $1}' | head -n 200); do
    echo "###TIMER:$u"
    systemctl show "$u" -p Id -p Description -p Unit -p ActiveState -p SubState -p UnitFileState -p NextElapseUSecRealtime -p LastTriggerUSec -p TimersCalendar -p TimersMonotonic -p FragmentPath 2>/dev/null
    svc=$(systemctl show "$u" -p Unit --value 2>/dev/null)
    if [ -n "$svc" ]; then
      echo "###TIMER_SERVICE:$svc"
      systemctl show "$svc" -p Id -p Description -p User -p WorkingDirectory -p ExecStart -p FragmentPath 2>/dev/null
    fi
  done
fi
echo __LITSSH_END__
""";

    public const string Sudo = """
echo __LITSSH_SUDO_META__
id -un 2>/dev/null
echo __LITSSH_SUDO_CRONTAB__
crontab -l 2>/dev/null
echo __LITSSH_SUDO_USERS__
if [ "$(id -u 2>/dev/null)" = "0" ]; then
  me=$(id -un 2>/dev/null)
  for u in $(cut -d: -f1 /etc/passwd 2>/dev/null); do
    [ "$u" = "$me" ] && continue
    c=$(crontab -u "$u" -l 2>/dev/null) || continue
    [ -z "$c" ] && continue
    printf '###USER:%s\n' "$u"
    printf '%s\n' "$c"
  done
fi
echo __LITSSH_SUDO_END__
""";
}

public static class ScheduledTasksProbeParser
{
    private const string MetaMarker = "__LITSSH_META__";
    private const string CurrentCrontabMarker = "__LITSSH_CURRENT_CRONTAB__";
    private const string EtcCrontabMarker = "__LITSSH_ETC_CRONTAB__";
    private const string CronDMarker = "__LITSSH_CRON_D__";
    private const string CronDirsMarker = "__LITSSH_CRON_DIRS__";
    private const string TimersMarker = "__LITSSH_TIMERS__";
    private const string EndMarker = "__LITSSH_END__";
    private const string SudoMetaMarker = "__LITSSH_SUDO_META__";
    private const string SudoCrontabMarker = "__LITSSH_SUDO_CRONTAB__";
    private const string SudoUsersMarker = "__LITSSH_SUDO_USERS__";
    private const string SudoEndMarker = "__LITSSH_SUDO_END__";

    public static ScheduledTasksProbeData Parse(string directOutput, string? sudoOutput)
    {
        var data = new ScheduledTasksProbeData();
        var direct = Normalize(directOutput);

        ParseMeta(Extract(direct, MetaMarker, CurrentCrontabMarker), data.Meta);
        data.CurrentCrontab = Extract(direct, CurrentCrontabMarker, EtcCrontabMarker);
        data.EtcCrontab = Extract(direct, EtcCrontabMarker, CronDMarker);
        ParseCronD(Extract(direct, CronDMarker, CronDirsMarker), data.CronDFiles);
        ParseCronDirs(Extract(direct, CronDirsMarker, TimersMarker), data.CronDirScripts);
        ParseTimers(Extract(direct, TimersMarker, EndMarker), data.Timers);

        if (string.IsNullOrWhiteSpace(data.EtcCrontab))
        {
            data.Warnings.Add("未读取到 /etc/crontab（可能不存在或当前用户无读取权限）。");
        }

        if (string.IsNullOrWhiteSpace(data.CrontabPath))
        {
            data.Warnings.Add("服务器未安装 crontab 命令。");
        }

        if (string.IsNullOrWhiteSpace(data.SystemctlPath))
        {
            data.Warnings.Add("服务器未安装 systemd/systemctl，未读取 systemd 定时器。");
        }

        if (!string.IsNullOrWhiteSpace(sudoOutput))
        {
            ParseSudo(Normalize(sudoOutput), data);
        }

        return data;
    }

    private static void ParseSudo(string text, ScheduledTasksProbeData data)
    {
        var user = Extract(text, SudoMetaMarker, SudoCrontabMarker).Trim();
        var crontab = Extract(text, SudoCrontabMarker, SudoUsersMarker);
        var users = Extract(text, SudoUsersMarker, SudoEndMarker);

        data.SudoSucceeded = user.Length > 0;
        data.ElevatedUser = user.Length > 0 ? user : null;
        data.ElevatedCrontab = crontab;

        var lines = users.Split('\n');
        var currentUser = string.Empty;
        var buffer = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("###USER:", StringComparison.Ordinal))
            {
                FlushUserCrontab(data, currentUser, buffer);
                currentUser = line["###USER:".Length..].Trim();
                buffer.Clear();
                continue;
            }

            if (currentUser.Length > 0)
            {
                buffer.Add(line);
            }
        }

        FlushUserCrontab(data, currentUser, buffer);
    }

    private static void FlushUserCrontab(ScheduledTasksProbeData data, string user, List<string> lines)
    {
        if (user.Length == 0)
        {
            return;
        }

        var text = string.Join('\n', lines).TrimEnd();
        if (text.Length > 0)
        {
            data.UserCrontabs.Add(new KeyValuePair<string, string>(user, text));
        }
    }

    private static void ParseMeta(string text, Dictionary<string, string> meta)
    {
        foreach (var line in text.Split('\n'))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            meta[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
    }

    private static void ParseCronD(string text, List<KeyValuePair<string, string>> files)
    {
        var currentPath = string.Empty;
        var buffer = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("###FILE:", StringComparison.Ordinal))
            {
                FlushFile(files, currentPath, buffer);
                currentPath = line["###FILE:".Length..].Trim();
                buffer.Clear();
                continue;
            }

            if (currentPath.Length > 0)
            {
                buffer.Add(line);
            }
        }

        FlushFile(files, currentPath, buffer);
    }

    private static void FlushFile(List<KeyValuePair<string, string>> files, string path, List<string> lines)
    {
        if (path.Length == 0)
        {
            return;
        }

        var text = string.Join('\n', lines).TrimEnd();
        files.Add(new KeyValuePair<string, string>(path, text));
    }

    private static void ParseCronDirs(string text, List<CronDirScriptInfo> scripts)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("###FILE:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["###FILE:".Length..].Trim();
            var parts = value.Split('|');
            if (parts.Length < 6)
            {
                continue;
            }

            scripts.Add(new CronDirScriptInfo
            {
                Path = parts[0],
                Mode = parts[1],
                Owner = parts[2],
                Group = parts[3],
                Size = long.TryParse(parts[4], out var size) ? size : 0,
                ModifiedUnixSeconds = long.TryParse(parts[5], out var modified) ? modified : 0
            });
        }
    }

    private static void ParseTimers(string text, List<SystemdTimerInfo> timers)
    {
        var unit = string.Empty;
        var serviceUnit = string.Empty;
        var timerBuffer = new List<string>();
        var serviceBuffer = new List<string>();

        void Flush()
        {
            if (unit.Length == 0)
            {
                return;
            }

            timers.Add(SystemdTimerParser.Parse(
                unit,
                string.Join('\n', timerBuffer),
                serviceUnit.Length > 0 ? string.Join('\n', serviceBuffer) : null));
            unit = string.Empty;
            serviceUnit = string.Empty;
            timerBuffer.Clear();
            serviceBuffer.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("###TIMER:", StringComparison.Ordinal))
            {
                Flush();
                unit = line["###TIMER:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("###TIMER_SERVICE:", StringComparison.Ordinal))
            {
                serviceUnit = line["###TIMER_SERVICE:".Length..].Trim();
                continue;
            }

            if (unit.Length == 0)
            {
                continue;
            }

            if (serviceUnit.Length > 0 && line.Contains('=') &&
                !line.StartsWith("Id=", StringComparison.Ordinal))
            {
                serviceBuffer.Add(line);
            }
            else
            {
                timerBuffer.Add(line);
            }
        }

        Flush();
    }

    private static string Extract(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += startMarker.Length;
        if (start < text.Length && text[start] == '\r')
        {
            start++;
        }

        if (start < text.Length && text[start] == '\n')
        {
            start++;
        }

        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? text[start..].TrimEnd() : text[start..end].TrimEnd();
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
