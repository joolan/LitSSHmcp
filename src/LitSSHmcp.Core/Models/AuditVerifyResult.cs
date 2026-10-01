namespace LitSSHmcp.Core.Models;

/// <summary>审计哈希链校验结果。</summary>
public sealed class AuditVerifyResult
{
    /// <summary>整条链是否完整且未被篡改。</summary>
    public bool Ok { get; set; }

    /// <summary>已校验的链记录数。</summary>
    public long Checked { get; set; }

    /// <summary>首个异常所在的链序号（校验失败时有值）。</summary>
    public long? FirstBadSeq { get; set; }

    /// <summary>结果说明（通过/失败原因）。</summary>
    public string Message { get; set; } = string.Empty;
}
