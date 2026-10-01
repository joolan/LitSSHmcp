using LitSSHmcp.Core.Models;

namespace LitSSHmcp.Core.Services.Security;

public enum CommandFilterResult
{
    Allowed,
    Sensitive,
    Blocked
}

public interface ICommandFilterService
{
    CommandFilterResult CheckCommand(string command);
}

public class CommandFilterService : ICommandFilterService
{
    private readonly ISecurityOptionsProvider _options;

    public CommandFilterService(ISecurityOptionsProvider options)
    {
        _options = options;
    }

    public CommandFilterResult CheckCommand(string command)
    {
        var config = _options.CommandFilter;

        if (config.IsBlocked(command))
            return CommandFilterResult.Blocked;

        if (config.IsSensitive(command))
            return CommandFilterResult.Sensitive;

        return CommandFilterResult.Allowed;
    }
}