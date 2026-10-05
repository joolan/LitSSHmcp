using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.ViewModels;

/// <summary>一位待发送的附件（图片给字节；文档给抽取文本）。</summary>
public sealed class AgentAttachment
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required AttachmentKind Kind { get; init; }
    public string? MediaType { get; init; }
    public byte[]? ImageBytes { get; init; }
    public string Text { get; init; } = string.Empty;

    public bool IsImage => Kind == AttachmentKind.Image;
}
