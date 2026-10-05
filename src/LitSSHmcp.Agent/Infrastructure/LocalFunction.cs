using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>自实现的 AIFunction：Name/Description/JsonSchema 固定，InvokeCoreAsync 直接调用委托（手动读参，兼容各连接器）。</summary>
internal sealed class LocalFunction : AIFunction
{
    private readonly Func<IDictionary<string, object?>, object?>? _impl;
    private readonly Func<IDictionary<string, object?>, CancellationToken, ValueTask<object?>>? _asyncImpl;

    public LocalFunction(string name, string description, string jsonSchema, Func<IDictionary<string, object?>, object?> impl)
    {
        Name = name;
        Description = description;
        JsonSchema = JsonDocument.Parse(jsonSchema).RootElement.Clone();
        _impl = impl;
    }

    public LocalFunction(string name, string description, string jsonSchema,
        Func<IDictionary<string, object?>, CancellationToken, ValueTask<object?>> impl)
    {
        Name = name;
        Description = description;
        JsonSchema = JsonDocument.Parse(jsonSchema).RootElement.Clone();
        _asyncImpl = impl;
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        _asyncImpl is not null
            ? _asyncImpl(arguments, cancellationToken)
            : new(_impl!(arguments));
}

/// <summary>从 AIFunctionArguments 安全读取参数（兼容 string / JsonElement，值可能来自不同连接器）。</summary>
internal static class ArgumentReader
{
    public static string? ReadString(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => value.ToString()
        };
    }

    public static int? ReadInt(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var n) => n,
            JsonElement je => int.TryParse(je.ToString(), out var p) ? p : null,
            string s => int.TryParse(s, out var q) ? q : null,
            _ => null
        };
    }
}
