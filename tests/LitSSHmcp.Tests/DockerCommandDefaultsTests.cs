using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Xunit;

namespace LitSSHmcp.Tests;

public class DockerCommandDefaultsTests
{
    [Fact]
    public async Task Default_config_includes_docker_danger_and_sensitive_rules()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new ConfigService(path, new DpapiSecretProtector());
            var config = await service.LoadConfigAsync();

            var filter = new CommandFilterService(new StaticOptions(config));
            Assert.Equal(CommandFilterResult.Blocked, filter.CheckCommand("docker system prune -af"));
            Assert.Equal(CommandFilterResult.Blocked, filter.CheckCommand("docker volume rm foo"));
            Assert.Equal(CommandFilterResult.Sensitive, filter.CheckCommand("docker restart order-svc"));
            Assert.Equal(CommandFilterResult.Sensitive, filter.CheckCommand("docker run -d nginx"));
            Assert.Equal(CommandFilterResult.Allowed, filter.CheckCommand("docker ps -a"));
            Assert.Equal(CommandFilterResult.Allowed, filter.CheckCommand("docker logs order-svc"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class StaticOptions : ISecurityOptionsProvider
    {
        private readonly Core.Models.AppConfig _config;
        public StaticOptions(Core.Models.AppConfig config) => _config = config;

        public Core.Models.CommandFilterConfig CommandFilter => _config.Security.CommandFilter;
        public Core.Models.SqlFilterConfig SqlFilter => _config.Security.SqlFilter;
        public Core.Models.FileTransferConfig FileTransfer => _config.Security.FileTransfer;
        public Core.Models.SshHostKeyConfig SshHostKey => _config.Security.SshHostKey;
        public Core.Models.DiscoveryConfig Discovery => _config.Security.Discovery;
        public Core.Models.LimitsConfig Limits => _config.Security.Limits;
        public Core.Models.AuditConfig Audit => _config.Security.Audit;
        public Core.Models.ApprovalConfig Approval => _config.Security.Approval;
        public void Invalidate() { }
    }
}
