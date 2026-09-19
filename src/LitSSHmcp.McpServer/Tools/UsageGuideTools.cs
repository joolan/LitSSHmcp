using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class UsageGuideTools
{
    [McpServerTool]
    [Description("Get usage guide and important notes for using LitSSH MCP tools")]
    public Task<string> GetUsageGuide()
    {
        var guide = new
        {
            title = "LitSSH MCP 使用指南",
            tools = new[]
            {
                new { name = "list_servers", description = "列出所有已配置的SSH服务器(用户问'连接了哪些服务器'时使用)" },
                new { name = "execute_command", description = "在指定服务器执行Shell命令(普通用户权限)" },
                new { name = "execute_with_sudo", description = "使用提权执行命令(主动提权,无需等待失败)" },
                new { name = "get_server_status", description = "获取服务器连接状态" },
                new { name = "test_connection", description = "测试SSH连接" },
                new { name = "get_command_history", description = "查看命令执行历史" },
                new { name = "upload_file", description = "上传本地文件到服务器" },
                new { name = "download_file", description = "从服务器下载文件到本地" },
                new { name = "list_remote_files", description = "浏览服务器目录" },
                new { name = "get_sudo_status", description = "获取服务器提权配置状态" },
                new { name = "get_usage_guide", description = "获取本使用指南" }
            },
            server_query_guide = new
            {
                description = "当用户询问服务器相关问题时,可以使用以下工具查询",
                when_to_use = new[]
                {
                    "用户问'连接了哪些服务器'或'有哪些服务器'时,使用 list_servers",
                    "用户问'某台服务器的状态'时,使用 get_server_status",
                    "用户问'服务器配置'或'服务器信息'时,使用 list_servers",
                    "用户想测试服务器连接时,使用 test_connection"
                },
                server_info_includes = "服务器名称、主机地址、端口、用户名、描述、标签、提权方式"
            },
            important_notes = new[]
            {
                "敏感命令(rm, chmod, reboot, shutdown等)需要用户在桌面弹窗中确认后才能执行",
                "被禁止的命令(rm -rf /, mkfs等)会直接拒绝执行",
                "所有命令执行结果都会记录到审计日志"
            },
            sudo_usage_guide = new
            {
                description = "【重要】主动判断是否需要提权,不要等命令失败后再提权",
                when_to_use_sudo = new[]
                {
                    "修改系统文件(如 /etc/ 目录下的配置)",
                    "管理系统服务(systemctl start/stop/restart)",
                    "安装或卸载软件包(apt/yum/dnf install/remove)",
                    "修改文件所有者或权限(chown/chmod系统目录)",
                    "管理用户和组(useradd/usermod/groupadd)",
                    "重启或关机(reboot/shutdown)",
                    "查看系统日志(/var/log/ 目录)",
                    "管理防火墙规则(iptables/firewalld)",
                    "挂载或卸载文件系统(mount/umount)",
                    "修改网络配置",
                    "管理Docker容器(如果docker需要sudo)",
                    "任何写入 /usr, /var, /etc, /opt 目录的操作"
                },
                how_to_use = new[]
                {
                    "1. 先用 get_sudo_status 检查服务器是否配置了提权",
                    "2. 如果已配置,直接使用 execute_with_sudo 执行命令",
                    "3. 如果未配置,使用 execute_command 尝试,失败后再提示用户需要提权"
                },
                example = "用户说'重启nginx'时,应该直接用 execute_with_sudo 执行 systemctl restart nginx,而不是先用 execute_command 失败后再提权"
            },
            error_handling = "执行命令时关注 success 和 error 字段。success=false 时检查 status 字段: blocked=被禁止, rejected=用户拒绝, server_not_found=服务器不存在, sudo_not_configured=未配置提权",
            sudo_types = "CurrentUser=使用SSH用户密码sudo(常用), RootUser=切换root(需root密码), CustomUser=切换指定用户(需该用户密码)",
            sensitive_commands = "rm, chmod, chown, reboot, shutdown, systemctl, kill, pkill, mount, umount",
            blocked_commands = "rm -rf /, mkfs, dd if=/dev/zero"
        };

        return Task.FromResult(JsonSerializer.Serialize(guide, new JsonSerializerOptions { WriteIndented = true }));
    }
}