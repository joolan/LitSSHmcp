namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>单个上游 DNS 的探测结果；LatencyMs 为完整请求往返耗时。</summary>
public sealed record DnsUpstreamProbeResult(
    string Endpoint,
    bool Success,
    int LatencyMs,
    int Rcode,
    string? Error)
{
    public string StatusText => Success ? "正常" : "失败";

    public string RcodeText => Rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => Rcode < 0 ? "-" : Rcode.ToString()
    };
}
