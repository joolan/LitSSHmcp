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
    void UpdateConfig(CommandFilterConfig config);
}

public class CommandFilterService : ICommandFilterService
{
    private CommandFilterConfig _config = new();

    public CommandFilterService(CommandFilterConfig config)
    {
        _config = config;
    }

    public CommandFilterResult CheckCommand(string command)
    {
        if (_config.IsBlocked(command))
            return CommandFilterResult.Blocked;

        if (_config.IsSensitive(command))
            return CommandFilterResult.Sensitive;

        return CommandFilterResult.Allowed;
    }

    public void UpdateConfig(CommandFilterConfig config)
    {
        _config = config;
    }
}