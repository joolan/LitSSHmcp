using System.Text.RegularExpressions;

namespace LitSSHmcp.Core.Models;

public class CommandFilterConfig
{
    public string[] BlockedCommands { get; set; } = Array.Empty<string>();
    public string[] SensitiveCommands { get; set; } = Array.Empty<string>();
    public string[] SensitivePatterns { get; set; } = Array.Empty<string>();

    private Regex[]? _compiledSensitivePatterns;

    public bool IsBlocked(string command)
    {
        var cmd = command.Trim().ToLowerInvariant();
        return BlockedCommands.Any(b => cmd.Contains(b.ToLowerInvariant()));
    }

    public bool IsSensitive(string command)
    {
        var cmd = command.Trim().ToLowerInvariant();

        if (SensitiveCommands.Any(s => cmd.Contains(s.ToLowerInvariant())))
            return true;

        _compiledSensitivePatterns ??= SensitivePatterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();

        return _compiledSensitivePatterns.Any(r => r.IsMatch(command));
    }
}