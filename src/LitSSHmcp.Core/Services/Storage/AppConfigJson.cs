using System.Text.Json;
using System.Text.Json.Serialization;

namespace LitSSHmcp.Core.Services.Storage;

/// <summary>
/// 配置文件统一的 JSON 序列化选项（camelCase + 枚举字符串）。
/// </summary>
public static class AppConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
