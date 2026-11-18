using System.Text;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>网络唤醒设备列表 CSV 导入/导出（name,mac,ip,broadcast,port,secureon,scheduleEnabled,scheduleTime,notes）。</summary>
public static class WakeOnLanCsv
{
    public const string Header = "name,mac,ip,broadcast,port,secureon,scheduleEnabled,scheduleTime,notes";

    public static string Export(IEnumerable<WakeOnLanHost> hosts)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var host in hosts)
        {
            sb.AppendLine(string.Join(",",
                Csv(host.Name), Csv(host.MacAddress), Csv(host.IpAddress), Csv(host.BroadcastAddress),
                host.Port.ToString(), Csv(host.SecureOnPassword),
                host.ScheduleEnabled ? "true" : "false", Csv(host.ScheduleTime), Csv(host.Notes)));
        }
        return sb.ToString();
    }

    public static IReadOnlyList<WakeOnLanHost> Import(string text)
    {
        var rows = Parse(text);
        if (rows.Count == 0)
            return Array.Empty<WakeOnLanHost>();

        var start = 0;
        if (rows[0].Length > 1 && string.Equals(rows[0][0].Trim(), "name", StringComparison.OrdinalIgnoreCase))
            start = 1;

        var result = new List<WakeOnLanHost>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Length == 0 || row.All(string.IsNullOrWhiteSpace))
                continue;
            var name = Field(row, 0);
            var mac = Field(row, 1);
            if (!WakeOnLanService.TryNormalizeMac(mac, out var normalized) || !seen.Add(normalized))
                continue;

            var port = int.TryParse(Field(row, 4), out var parsedPort) && parsedPort is >= 1 and <= 65535
                ? parsedPort
                : WakeOnLanService.DefaultPort;
            var time = Field(row, 7);
            if (!IsValidTime(time))
                time = string.Empty;
            var secureOn = Field(row, 5);
            if (secureOn.Length > 0)
            {
                secureOn = WakeOnLanService.TryNormalizeMac(secureOn, out var normalizedSecureOn)
                    ? normalizedSecureOn
                    : string.Empty;
            }

            result.Add(new WakeOnLanHost
            {
                Name = string.IsNullOrWhiteSpace(name) ? normalized : name,
                MacAddress = normalized,
                IpAddress = Field(row, 2),
                BroadcastAddress = Field(row, 3),
                Port = port,
                SecureOnPassword = secureOn,
                ScheduleEnabled = ParseBool(Field(row, 6)),
                ScheduleTime = time,
                Notes = Field(row, 8)
            });
        }
        return result;
    }

    public static bool IsValidTime(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", null, out var time) &&
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    private static string Field(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index].Trim() : string.Empty;

    private static bool ParseBool(string value) =>
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "是";

    private static string Csv(string? value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            rows.Add(row.ToArray());
            row.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    EndRow();
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
            EndRow();
        return rows;
    }
}
