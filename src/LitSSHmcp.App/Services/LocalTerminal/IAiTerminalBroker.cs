using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Services.LocalTerminal;

public sealed record AiTerminalInfo(
    string Id,
    string Name,
    string Status,
    int ProcessId,
    bool Running,
    int? ExitCode,
    string Command,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastOutputAt);

public sealed record AiTerminalOpenResult(bool Success, AiTerminalInfo? Info, string? Error);

public sealed record AiTerminalReadResult(string Text, long NextCursor, bool Truncated, AiTerminalInfo Info);

public sealed record AiTerminalStopResult(bool Success, string Status, string Message);

public sealed record AiTerminalRunResult(bool Success, CommandResult? Result, string? Error);

public interface IAiTerminalBroker
{
    Task<AiTerminalOpenResult> OpenAsync(
        string sessionKey,
        string name,
        string command,
        TimeSpan maxRuntime,
        int maxSessions,
        CancellationToken ct = default);

    IReadOnlyList<AiTerminalInfo> List(string sessionKey);

    AiTerminalReadResult? Read(string sessionKey, string terminalId, long cursor, int maxChars);

    Task<AiTerminalRunResult> RunAsync(
        string sessionKey,
        string terminalId,
        string command,
        TimeSpan timeout,
        CancellationToken ct = default);

    Task<AiTerminalStopResult> StopAsync(string sessionKey, string terminalId, CancellationToken ct = default);

    void CloseTerminal(LocalTerminalSessionViewModel terminal);

    void CloseSession(string sessionKey);

    void CloseAll();
}
