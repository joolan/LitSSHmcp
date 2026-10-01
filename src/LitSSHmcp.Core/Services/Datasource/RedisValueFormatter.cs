using System.Text.Json;

namespace LitSSHmcp.Core.Services.Datasource;

public sealed record RedisJsonResult(object? Value, bool Truncated);

/// <summary>把 RESP 回复转成适合 JSON 输出的 CLR 结构，并做长度/条数截断。</summary>
public static class RedisValueFormatter
{
    public const int MaxStringLength = 4096;

    private sealed class State
    {
        public State(int maxItems) => MaxItems = maxItems;
        public int MaxItems { get; }
        public bool Truncated { get; set; }
    }

    public static RedisJsonResult Format(RedisValue value, int maxItems)
    {
        var state = new State(Math.Max(1, maxItems));
        return new RedisJsonResult(Convert(value, state), state.Truncated);
    }

    /// <summary>标量/数组转紧凑字符串（用于表格化输出）。</summary>
    public static string ToCompactString(RedisValue value, int maxItems = 50)
    {
        var (result, _) = Format(value, maxItems);
        return result switch
        {
            null => string.Empty,
            string text => text,
            long number => number.ToString(),
            _ => JsonSerializer.Serialize(result)
        };
    }

    private static object? Convert(RedisValue value, State state)
    {
        switch (value.Kind)
        {
            case RedisValueKind.Null:
                return null;
            case RedisValueKind.Integer:
                return value.Integer;
            case RedisValueKind.SimpleString:
            case RedisValueKind.BulkString:
                return TruncateText(value.Text ?? string.Empty, state);
            case RedisValueKind.Error:
                return "[error] " + (value.Text ?? string.Empty);
            case RedisValueKind.Array:
            {
                var items = value.Items ?? Array.Empty<RedisValue>();
                var list = new List<object?>(Math.Min(items.Count, state.MaxItems));
                for (var i = 0; i < items.Count && i < state.MaxItems; i++)
                    list.Add(Convert(items[i], state));
                if (items.Count > state.MaxItems) state.Truncated = true;
                return list;
            }
            default:
                return value.ToDisplayString();
        }
    }

    private static string TruncateText(string text, State state)
    {
        if (text.Length <= MaxStringLength) return text;
        state.Truncated = true;
        return text[..MaxStringLength] + $"...[已截断, 原长 {text.Length} 字符]";
    }
}
