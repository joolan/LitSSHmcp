// 【同步约定 · 请勿删除】本文件返回的"内置工具清单 + 意图路由指南":
//   ① tools 清单: 由本程序集所有 [McpServerTool] 方法反射生成(name + [Description]), 无需手工维护;
//   ② 意图路由/说明文案: 手工维护, 新增/改名/删除工具时需同步更新本文件文案与 docs/TOOLS.md;
//   ③ docs/TOOLS.md 是工具说明的唯一事实来源(桌面 App「MCP工具说明」展示其嵌入副本),
//      一致性由测试工程 LitSSHmcp.McpServer.Tests 校验(注册工具 ↔ docs/TOOLS.md ↔ 本指南)。
// [Description] 只保留 1~2 句(做什么 + 关键互斥提示), 详细"何时用/不要用"以 docs/TOOLS.md 的意图路由表为准。
// 工具命名遵循 docs/TOOLS.md「工具命名约定」: 小写 snake_case, 域前缀(ssh/datasource/mysql/redis/topology/mcp)。
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace LitSSHmcp.McpServer.Tools;

[McpServerToolType]
public class UsageGuideTools
{
    [McpServerTool(Name = "mcp_usage_guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("获取LitSSH MCP使用指南(全部工具清单+意图路由+注意事项)。不确定用哪个工具时先调它")]
    public Task<string> GetUsageGuide()
    {
        var guide = new
        {
            title = "LitSSH MCP 使用指南",
            tools = EnumerateTools(),
            server_query_guide = new
            {
                description = "当用户询问服务器相关问题时,可以使用以下工具查询",
                when_to_use = new[]
                {
                    "用户问'连接了哪些服务器'或'有哪些服务器'时,使用 ssh_list_servers",
                    "用户问'某台服务器的状态'时,使用 ssh_get_server_status",
                    "用户问'服务器配置'或'服务器信息'时,使用 ssh_list_servers",
                    "用户想测试服务器连接时,使用 ssh_test_connection",
                    "用户要求在服务器上执行命令/查日志/看进程/查磁盘网络时,使用 ssh_execute_command",
                    "用户要求重启或启停服务、改系统配置、安装软件、改权限、管理用户、看系统日志等需要root权限时,主动使用 ssh_execute_sudo",
                    "用户问'这台机器能不能sudo/有没有配置提权'时,使用 ssh_get_sudo_status",
                    "用户问'执行过哪些命令/命令历史/操作记录'时,使用 ssh_get_command_history",
                    "用户要求上传或下载文件时,使用 ssh_upload_file / ssh_download_file",
                    "用户想看服务器上的目录或文件时,使用 ssh_list_files"
                },
                server_info_includes = "服务器名称、主机地址、端口、用户名、描述、标签、提权方式"
            },
            datasource_query_guide = new
            {
                description = "当用户询问MySQL/Redis/数据库相关问题时,按意图选择工具",
                when_to_use = new[]
                {
                    "用户问'有哪些MySQL/数据库列表/数据库配置信息'时,使用 datasource_list",
                    "用户问'数据库能不能连上/测试数据库连接/数据库连不上'时,使用 datasource_test_connection",
                    "用户问'MySQL服务正不正常/数据库为什么慢/连接数暴涨'时,先用 mysql_diagnostics",
                    "用户想查数据/看表结构/查processlist时,使用 mysql_query",
                    "用户想改数据/建表/加字段时,使用 mysql_execute",
                    "用户问'这条SQL为什么慢'时,使用 mysql_explain",
                    "用户问'PostgreSQL/PG正不正常/PG为什么慢/连接数暴涨'时,先用 postgres_diagnostics",
                    "用户想查PG数据/看表结构时,使用 postgres_query; 改PG数据/建表时,使用 postgres_execute",
                    "用户问'这条PG SQL为什么慢'时,使用 postgres_explain",
                    "用户问'Redis正不正常/缓存为什么慢/内存涨/命中率低'时,先用 redis_diagnostics",
                    "用户想查缓存值/看key/看集合内容/看Redis慢日志时,使用 redis_read(只读白名单命令)",
                    "用户想写缓存/删key/设过期/改Redis配置时,使用 redis_execute(一律弹桌面确认, 危险命令直接拒绝)",
                    "用户问'谁执行了什么SQL/Redis命令/审计记录'时,使用 datasource_get_sql_history",
                    "注意区分: SSH服务器列表=ssh_list_servers, SSH连通性=ssh_test_connection; 数据库列表=datasource_list, 数据库连通性=datasource_test_connection; SSH命令=ssh_execute_command, SQL=mysql_query/mysql_execute, Redis只读=redis_read, Redis写=redis_execute"
                },
                how_to_get_id = "所有数据库/Redis工具的 datasourceId 用 datasource_list 获取(type=redis 的数据源用 Redis 工具); 服务器 serverId 用 ssh_list_servers 获取"
            },
            important_notes = new[]
            {
                "敏感命令(rm, chmod, reboot, shutdown等)需要用户在桌面弹窗中确认后才能执行",
                "被禁止的命令(rm -rf /, mkfs等)会直接拒绝执行",
                "所有命令执行结果都会记录到审计日志",
                "数据库账号密码永远不会出现在任何工具入参/出参中, AI只能通过 datasourceId 引用数据源",
                "写SQL(mysql_execute)同样经过安全过滤: 无WHERE的DELETE/UPDATE、DROP等直接拒绝, 其余敏感语句需用户桌面确认",
                "Redis写操作(redis_execute)第一期一律需要用户桌面确认; FLUSHALL/SHUTDOWN/DEBUG等危险与阻塞类命令直接拒绝(不会执行)",
                "所有SQL与Redis命令操作(含被拒绝的)都会写入审计日志"
            },
            topology_guide = new
            {
                description = "【重要】跨服务器/应用/数据库的故障排查要先看拓扑, 再分侧取证",
                workflow = new[]
                {
                    "1. topology_get_overview 获取全局拓扑: 哪些Java应用跑在哪台SSH服务器、连了哪些MySQL",
                    "2. 用户报障某应用时, 用 topology_get_dependencies(app:应用ID) 拿到其所在服务器和依赖的数据源",
                    "3. SSH侧取证: ssh_execute_command 查看 java 进程/端口/应用日志(netstat/ss/journalctl/tail)",
                    "4. 数据库侧取证: mysql_diagnostics 看连接数/慢查询/锁/复制, mysql_query 查 processlist 和慢日志, mysql_explain 分析问题SQL; 涉及缓存时用 redis_diagnostics 看内存/命中率/慢日志, redis_read 查具体key",
                    "5. 跨机关联: 应用日志或配置文件中的数据库IP, 与 datasource_list 返回的 host/port 对应即可定位是哪个数据源",
                    "6. 拓扑缺失或过期时, 调用 topology_discover 扫描(java进程/网络连接/JDBC配置/MySQL processlist)自动补全关系",
                    "7. 拓扑中的边: runsOn=应用/数据库运行在服务器(应用与MySQL都可有), connectsTo=应用连接数据库, canAccess=服务器可访问数据库"
                },
                asset_id_format = "节点ID格式: ssh:服务器ID, ds:数据源ID, app:应用ID; topology_get_dependencies 也接受纯ID或名称",
                relation_sources = "拓扑边来自两部分: 配置文件人工声明(relations) + topology_discover 自动发现(缓存于本机SQLite)"
            },
            file_transfer_guide = new
            {
                description = "文件传输(上传/下载/列目录)使用说明",
                points = new[]
                {
                    "上传=ssh_upload_file, 下载=ssh_download_file, 列远程目录=ssh_list_files(是远程服务器目录, 不是本机文件)",
                    "三者都受 fileTransfer 开关与本地/远程路径白名单约束, 越界返回 path_not_allowed",
                    "上传/下载需用户在桌面确认"
                }
            },
            datasource_guide = new
            {
                description = "数据源(MySQL/Redis)使用说明",
                points = new[]
                {
                    "'看有哪些数据库'→datasource_list, '测数据库能否连上'→datasource_test_connection, '查数据'→mysql_query, '改数据'→mysql_execute",
                    "'PG查数据'→postgres_query, 'PG改数据'→postgres_execute, 'PG体检'→postgres_diagnostics",
                    "'查缓存/读key'→redis_read, '写缓存/删key/设过期'→redis_execute, 'Redis体检'→redis_diagnostics",
                    "datasource_list 返回 host/port/username 可以直接用于与应用日志中的连接串比对; type=redis 的数据源只能用 Redis 工具",
                    "mysql_query 只允许只读语句(SELECT/SHOW/EXPLAIN/DESC/WITH), 单条语句, 结果最多1000行",
                    "mysql_execute 用于 INSERT/UPDATE/DELETE/DDL, 会触发安全策略与用户审批",
                    "redis_read 只允许只读白名单命令, 结果已JSON化并截断超长字符串/超大数组; 非白名单命令会提示改用 redis_execute",
                    "redis_execute 的写命令一律弹桌面确认, 危险命令(清库/关服/换主从/加载模块/阻塞连接类)直接拒绝",
                    "数据源 accessMode=sshTunnel 时数据库只能经指定SSH服务器访问, MCP会自动建隧道(Redis同样适用)",
                    "凭据由用户通过管理界面或配置文件录入, 任何工具都不会返回密码"
                }
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
                    "1. 先用 ssh_get_sudo_status 检查服务器是否配置了提权",
                    "2. 如果已配置,直接使用 ssh_execute_sudo 执行命令",
                    "3. 如果未配置,使用 ssh_execute_command 尝试,失败后再提示用户需要提权"
                },
                example = "用户说'重启nginx'时,应该直接用 ssh_execute_sudo 执行 systemctl restart nginx,而不是先用 ssh_execute_command 失败后再提权"
            },
            error_handling = "执行命令时关注 success 和 error 字段。success=false 时检查 status 字段: blocked=被禁止, rejected=用户拒绝, not_readonly/readonly_statement=命令与工具不匹配(只读用mysql_query/redis_read, 写用mysql_execute/redis_execute), server_not_found=服务器不存在, sudo_not_configured=未配置提权",
            sudo_types = "CurrentUser=使用SSH用户密码sudo(常用), RootUser=切换root(需root密码), CustomUser=切换指定用户(需该用户密码)",
            sensitive_commands = "rm, chmod, chown, reboot, shutdown, systemctl, kill, pkill, mount, umount",
            blocked_commands = "rm -rf /, mkfs, dd if=/dev/zero"
        };

        return Task.FromResult(JsonSerializer.Serialize(guide, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// 反射本程序集中所有带 <see cref="McpServerToolAttribute"/> 的方法, 生成工具清单(名称 + <see cref="DescriptionAttribute"/>)。
    /// 使 get_usage_guide 的工具清单始终与已注册工具一致, 消除"手工清单"漂移。
    /// </summary>
    private static object[] EnumerateTools() =>
        typeof(UsageGuideTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => new { Tool = m.GetCustomAttribute<McpServerToolAttribute>(), Method = m })
            .Where(x => x.Tool is not null)
            .Select(x => new
            {
                name = x.Tool!.Name ?? x.Method.Name,
                description = x.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty
            })
            .OrderBy(x => x.name, StringComparer.Ordinal)
            .Cast<object>()
            .ToArray();
}
