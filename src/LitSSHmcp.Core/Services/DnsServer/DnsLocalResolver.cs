namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>本地记录查询结果：Matched=false 表示继续缓存/上游流程。</summary>
public sealed record DnsLocalLookup(
    bool Matched,
    IReadOnlyList<DnsLocalRecord> Records);

/// <summary>
/// 本地 A/AAAA/CNAME/PTR 记录。支持精确名称与 *.example.com 通配符；
/// 名称存在但类型不匹配时返回 NODATA，不向上游泄露内网域名。
/// </summary>
public sealed class DnsLocalResolver
{
    private readonly Dictionary<string, List<DnsLocalRecord>> _records =
        new(StringComparer.OrdinalIgnoreCase);

    public DnsLocalResolver(IEnumerable<DnsLocalRecord> records)
    {
        foreach (var record in records)
        {
            if (!record.Enabled)
                continue;
            var name = DnsWire.NormalizeName(record.Name);
            if (name.Length == 0)
                continue;
            if (!_records.TryGetValue(name, out var list))
            {
                list = new List<DnsLocalRecord>();
                _records[name] = list;
            }
            list.Add(record);
        }
    }

    public int Count => _records.Count;

    public DnsLocalLookup Lookup(string name, ushort queryType)
    {
        var normalized = DnsWire.NormalizeName(name);
        if (TryLookup(normalized, queryType, out var result))
            return result;

        var index = normalized.IndexOf('.');
        while (index >= 0)
        {
            var suffix = normalized[(index + 1)..];
            if (suffix.Length > 0 && TryLookup("*." + suffix, queryType, out result))
                return result;
            index = normalized.IndexOf('.', index + 1);
        }

        return new DnsLocalLookup(false, Array.Empty<DnsLocalRecord>());
    }

    private bool TryLookup(string name, ushort queryType, out DnsLocalLookup result)
    {
        if (!_records.TryGetValue(name, out var records))
        {
            result = new DnsLocalLookup(false, Array.Empty<DnsLocalRecord>());
            return false;
        }

        if (queryType == 255)
        {
            result = new DnsLocalLookup(true, records);
            return true;
        }

        var matching = records
            .Where(record => TypeToUshort(record.Type) == queryType)
            .ToList();

        if (matching.Count == 0 && queryType != 5)
        {
            var cname = records.FirstOrDefault(record =>
                string.Equals(record.Type, "CNAME", StringComparison.OrdinalIgnoreCase));
            if (cname is not null)
                matching.Add(cname);
        }

        result = new DnsLocalLookup(true, matching);
        return true;
    }

    public static ushort TypeToUshort(string type) => type.ToUpperInvariant() switch
    {
        "A" => 1,
        "CNAME" => 5,
        "PTR" => 12,
        "AAAA" => 28,
        _ => 0
    };
}
