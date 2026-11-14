using System.Text.Encodings.Web;
using System.Text.Json;

namespace LitSSHmcp.McpServer.Services;

/// <summary>用户/模型可见 JSON 输出：保留中文、URL、时间偏移等可读性，避免默认 encoder 产生 \uXXXX。</summary>
internal static class JsonOutput
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
