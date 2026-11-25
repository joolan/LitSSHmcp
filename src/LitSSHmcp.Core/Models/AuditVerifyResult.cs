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

    /// <summary>
    /// 校验通过但使用无密钥 SHA-256 签名的历史记录数（强度降级）。
    /// 这些记录内容未被改动，但仍可被掌握 audit.db 的人无密钥改写。
    /// </summary>
    public long WeakUnkeyed { get; set; }

    /// <summary>缺少签名密钥（audit.db.key）导致无法校验 HMAC 记录；不能据此判定被篡改。</summary>
    public bool MissingKey { get; set; }

    /// <summary>结果说明（通过/失败原因）。</summary>
    public string Message { get; set; } = string.Empty;
}
