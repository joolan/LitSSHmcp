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
                    "用户要看服务器整体态势(资源占用/端口进程服务三元组/nginx证书到期/systemd健康)时, 先 ssh_snapshot_get 查已存快照; 需要最新数据用 ssh_snapshot_refresh(较慢,同一服务器同时只允许一个快照)",
                    "用户问'服务器配置'或'服务器信息'时,使用 ssh_list_servers",
                    "用户想测试服务器连接时,使用 ssh_test_connection",
                    "用户要求在服务器上执行命令/查日志/看进程/查磁盘网络时,使用 ssh_execute_command",
                    "用户要求重启或启停服务、改系统配置、安装软件、改权限、管理用户、看系统日志等需要root权限时,主动使用 ssh_execute_sudo",
                    "用户问'这台机器能不能sudo/有没有配置提权'时,使用 ssh_get_sudo_status",
                    "用户问'执行过哪些命令/命令历史/操作记录'时,使用 ssh_get_command_history",
                    "用户要求上传或下载文件时,使用 ssh_upload_file / ssh_download_file; 上传/下载**整个文件夹或多个文件**时用 ssh_upload_files / ssh_download_files(一条 SFTP 连接、整批一次审批)",
                    "用户想看服务器上的目录或文件时,使用 ssh_list_files",
                    "用户问容器(有哪些容器/容器状态/端口/日志/资源/镜像)时,使用 docker_ps / docker_logs / docker_inspect / docker_stats / docker_images; 重启容器用 docker_restart, 进入容器执行用 docker_exec(均需确认)",
                    "用户问systemd服务状态或要重启服务/看服务日志时,使用 service_status / service_restart / service_logs; 不确定服务名先用 service_list",
                    "用户要看日志文件尾部或在日志里搜错误/关键字时,使用 log_tail / log_grep(可传 path, 也可传 appId 用应用管理里配置的日志路径); 不知道路径时先用 log_find 发现最近修改的 .log 文件(路径受 security.logs.allowedPaths 约束)",
                    "用户问Java进程/线程/死锁/CPU飙高/内存/GC时,先用 java_processes 拿pid(可传appId过滤), 再用 java_threads / java_heap / java_info(也可直接传appId自动解析pid)",
                    "用户要对某个应用做整体体检(跨服务器+依赖数据库)时,优先用 app_health_snapshot, 再按需下钻",
                    "用户问'审计记录是哪个AI客户端/哪次会话产生的'时,用 mcp_list_sessions 看会话(客户端名称/版本), 再用 ssh_get_command_history / datasource_get_sql_history 的 sessionId 过滤具体记录; 当前会话ID见 mcp_self_check"
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
                how_to_get_id = "服务器 serverId 用 ssh_list_servers 获取, 且 ID/名称/主机名三种写法都能被工具识别; 数据源 datasourceId 用 datasource_list 获取(ID 或名称均可, type=redis 的数据源用 Redis 工具)。传错时错误信息会回显可用 ID, 照抄即可",
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
                    "上传=ssh_upload_file, 下载=ssh_download_file, 列远程目录=ssh_list_files(是远程服务器目录, 不是本机文件); 文件夹/多文件批量用 ssh_upload_files / ssh_download_files(单连接、整批一次审批、保留子目录层级)",
                    "三者都受 fileTransfer 开关与本地/远程路径白名单约束, 越界返回 path_not_allowed(错误里会列出允许的路径)",
                    "上传/下载需审批(默认桌面+CLI双通道), 拒绝/超时分别返回 rejected / approval_timeout",
                    "列目录失败(路径不存在/无权限)会明确返回失败状态, 不会用空列表冒充'目录为空'; 条目过多会置 truncated=true"
                }
            },
            datasource_guide = new
            {
                description = "数据源(MySQL/PostgreSQL/Redis)使用说明",
                points = new[]
                {
                    "'看有哪些数据库'→datasource_list, '测数据库能否连上'→datasource_test_connection, '查数据'→mysql_query, '改数据'→mysql_execute",
                    "'PG查数据'→postgres_query, 'PG改数据'→postgres_execute, 'PG体检'→postgres_diagnostics",
                    "'查缓存/读key'→redis_read, '写缓存/删key/设过期'→redis_execute, 'Redis体检'→redis_diagnostics",
                    "datasource_list 返回 host/port/username 可以直接用于与应用日志中的连接串比对; type=redis 的数据源只能用 Redis 工具",
                    "mysql_query 只允许只读语句(SELECT/SHOW/EXPLAIN/DESC/WITH), 单条语句, 结果最多1000行; 数据修改型CTE(WITH ... DELETE/UPDATE)与 EXPLAIN ANALYZE <DML> 会被识别为写操作并拒绝(请改用 mysql_execute)",
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
                    "判断需要root权限时, 直接使用 ssh_execute_sudo, 不必先用 ssh_get_sudo_status 或普通命令试探",
                    "ssh_execute_sudo 总是需要用户确认; 未配置提权会明确返回 sudo_not_configured",
                    "ssh_get_sudo_status 只在需要向用户解释'为什么不能提权'时使用"
                },
                example = "用户说'重启nginx'时, 直接用 ssh_execute_sudo 执行 systemctl restart nginx, 而不是先 ssh_execute_command 失败后再提权"
            },
            error_handling = "看 success 与 status 字段判断失败类型并决定是否重试: blocked=被安全策略禁止(不要重试); rejected=用户拒绝(不要重试); approval_timeout/approval_unavailable=审批超时或不可用(可提示用户后用同一命令重试); server_not_found/datasource_not_found=标识不存在(改用列表工具拿正确ID, 错误信息里已回显可用ID); readonly_statement=只读语句用错了写工具(改用mysql_query/postgres_query/redis_read); not_readonly_statement=写语句用错了只读工具(改用*_execute); sudo_not_configured=未配置提权; snapshot_not_found=该服务器还没快照(用ssh_snapshot_refresh生成); snapshot_in_progress=该服务器已有快照在采集(不要重复刷新,稍后用ssh_snapshot_get查); auth_failed=账号/密钥错; host_key_mismatch=主机密钥变化(可能是安全事件, 先人工核对指纹, 不要重试); timeout/rate_limited/connection_error=可稍后重试",
            sudo_types = "Auto=自动(先sudo,失败再su - root,推荐), CurrentUser=使用SSH用户密码sudo(常用), RootUser=切换root(需root密码), CustomUser=切换指定用户(需该用户密码)",
            sensitive_commands = "rm, chmod, chown, reboot, shutdown, systemctl stop/restart, kill, pkill, mount, umount, 以及 docker rm/rmi/run/exec/stop/restart/compose down 等写操作",
            blocked_commands = "rm -rf /, mkfs, dd if=/dev/zero, docker system prune, docker volume rm, docker run --privileged 等"
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
