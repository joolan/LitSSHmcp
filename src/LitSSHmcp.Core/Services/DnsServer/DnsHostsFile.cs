using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LitSSHmcp.Core.Services.DnsServer;

public sealed record DnsHostsParseResult(
    IReadOnlyList<DnsLocalRecord> Records,
    IReadOnlyList<string> Errors);

/// <summary>
/// hosts 文件格式的本地记录导入/导出。
/// 导入支持 IP + 多个主机名、# 注释与空行；导出只包含 A/AAAA，CNAME/PTR 无法用 hosts 表示。
/// </summary>
public static class DnsHostsFile
{
    public static DnsHostsParseResult Parse(string text, int defaultTtl = 300)
    {
        var records = new List<DnsLocalRecord>();
        var errors = new List<string>();
        if (string.IsNullOrEmpty(text))
            return new DnsHostsParseResult(records, errors);

        var lineNumber = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = raw;
            var comment = line.IndexOf('#');
            if (comment >= 0)
                line = line[..comment];
            line = line.Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                errors.Add($"第 {lineNumber} 行：缺少主机名");
                continue;
            }

            if (!IPAddress.TryParse(parts[0], out var address))
            {
                errors.Add($"第 {lineNumber} 行：IP“{parts[0]}”不合法");
                continue;
            }

            var type = address.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A";
            for (var i = 1; i < parts.Length; i++)
            {
                var name = parts[i];
                if (!DnsWire.IsValidRecordName(name))
                {
                    errors.Add($"第 {lineNumber} 行：主机名“{name}”不合法");
                    continue;
                }

                records.Add(new DnsLocalRecord
                {
                    Name = DnsWire.NormalizeName(name),
                    Type = type,
                    Value = address.ToString(),
                    Ttl = Math.Clamp(defaultTtl, 0, 86400),
                    Enabled = true
                });
            }
        }

        return new DnsHostsParseResult(records, errors);
    }

    public static string Export(IEnumerable<DnsLocalRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# LitSSH DNS 本地记录导出（hosts 格式）");
        builder.AppendLine("# 仅包含启用的 A / AAAA 记录；CNAME / PTR 无法用 hosts 格式表示。");
        foreach (var record in records)
        {
            if (!record.Enabled)
                continue;
            if (record.Type is not ("A" or "AAAA"))
                continue;
            builder.AppendLine($"{record.Value}\t{DnsWire.NormalizeName(record.Name)}");
        }
        return builder.ToString().TrimEnd();
    }
}
