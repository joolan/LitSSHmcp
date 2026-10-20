using System.Text;
using System.Text.RegularExpressions;

namespace LitSSHmcp.App.Services.LocalTerminal;

public sealed record TerminalOutputSnapshot(string Text, long NextCursor, bool Truncated, DateTimeOffset? LastWriteAt);

public sealed class TerminalOutputBuffer
{
    private static readonly Regex AnsiCsi = new("\u001B\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex AnsiOsc = new("\u001B\\][^\u0007]*(?:\u0007|\u001B\\\\)", RegexOptions.Compiled);
    private static readonly Regex TerminalMarker = new(@"<<<LITSSH_[0-9a-fA-F]{32}>>>[^\r\n]*(?:\r\n|\r|\n)?", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Queue<Chunk> _chunks = new();
    private readonly int _capacity;
    private long _nextOffset;
    private int _chars;
    private DateTimeOffset? _lastWriteAt;

    public TerminalOutputBuffer(int capacityChars = 262144)
        => _capacity = Math.Max(4096, capacityChars);

    public long Cursor
    {
        get { lock (_gate) return _nextOffset; }
    }

    public DateTimeOffset? LastWriteAt
    {
        get { lock (_gate) return _lastWriteAt; }
    }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (_gate)
        {
            _chunks.Enqueue(new Chunk(_nextOffset, text));
            _nextOffset += text.Length;
            _chars += text.Length;
            _lastWriteAt = DateTimeOffset.UtcNow;
            while (_chars > _capacity && _chunks.Count > 1)
            {
                var old = _chunks.Dequeue();
                _chars -= old.Text.Length;
            }
        }
    }

    public TerminalOutputSnapshot Read(long cursor, int maxChars)
    {
        if (maxChars <= 0)
            maxChars = 8192;

        lock (_gate)
        {
            var truncated = false;
            var start = Math.Clamp(cursor, 0, _nextOffset);
            if (_chunks.Count > 0 && start < _chunks.Peek().Start)
            {
                start = _chunks.Peek().Start;
                truncated = true;
            }

            var end = Math.Min(_nextOffset, start + maxChars);
            if (end < _nextOffset)
                truncated = true;

            var sb = new StringBuilder();
            foreach (var chunk in _chunks)
            {
                var chunkEnd = chunk.Start + chunk.Text.Length;
                if (chunkEnd <= start)
                    continue;
                if (chunk.Start >= end)
                    break;

                var from = (int)(Math.Max(start, chunk.Start) - chunk.Start);
                var length = (int)(Math.Min(end, chunkEnd) - Math.Max(start, chunk.Start));
                sb.Append(chunk.Text, from, length);
            }

            return new TerminalOutputSnapshot(Clean(sb.ToString()), end, truncated, _lastWriteAt);
        }
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var cleaned = AnsiCsi.Replace(text, string.Empty);
        cleaned = AnsiOsc.Replace(cleaned, string.Empty);
        cleaned = TerminalMarker.Replace(cleaned, string.Empty);
        return cleaned.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private readonly record struct Chunk(long Start, string Text);
}
