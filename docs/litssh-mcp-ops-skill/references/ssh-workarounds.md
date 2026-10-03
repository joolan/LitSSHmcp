# SSH 变通手册（MCP 未覆盖能力）

> 本文件是 `SKILL.md` 第 6 节的展开，**按需查阅**。
> 当对应领域工具未启用、或 MCP 没有该能力时，用 `ssh_execute_command`（只读排查）或 `ssh_execute_sudo`（需提权，触发审批）实现。
> **先只读、后写入；写入前先拿到证据并征得确认。**

## 6.1 定位/读取日志（MCP 没覆盖的路径）
```
# 看启动参数找日志配置
ssh_execute_command(serverId, "ps -eo pid,args --no-headers | grep '[j]ava' | head")
# 找配置文件
ssh_execute_command(serverId, "find /opt/order -maxdepth 3 \\( -name 'application*.yml' -o -name 'application*.properties' -o -name 'logback*.xml' -o -name 'log4j2*.xml' \\) 2>/dev/null")
# 读配置里的日志路径
ssh_execute_command(serverId, "grep -nE 'logging\\.(file|path)|log\\.path|LOG_PATH' /opt/order/config/application.yml 2>/dev/null")
# 直接读日志（若 log_tail 白名单不包含该路径，可用 tail 变通；仍受命令过滤）
ssh_execute_command(serverId, "tail -n 200 /opt/order/logs/app.log")
# 按关键字检索
ssh_execute_command(serverId, "grep -n -i -- 'OutOfMemory\\|Exception' /opt/order/logs/app.log | tail -n 100")
```
> 若路径应长期使用，建议把该目录加入 `security.logs.allowedPaths`（安全设置窗口），或在该应用填「日志路径」，之后就能用 `log_tail`/`log_grep`。

## 6.2 端口/进程/资源
```
ssh_execute_command(serverId, "ss -ltnp 2>/dev/null | grep -E ':8080|:80' || netstat -ltnp")
ssh_execute_command(serverId, "ps -eo pid,ppid,pcpu,pmem,etime,args --sort=-pcpu | head -20")
ssh_execute_command(serverId, "free -m; echo ---; df -h; echo ---; df -i")
ssh_execute_command(serverId, "du -sh /var/log/* 2>/dev/null | sort -h | tail -20")
```

## 6.3 HTTP/健康检查/依赖探测
```
ssh_execute_command(serverId, "curl -s -m 5 http://127.0.0.1:8080/actuator/health")
ssh_execute_command(serverId, "curl -s -m 5 -o /dev/null -w '%{http_code}\\n' http://127.0.0.1:8080/")
ssh_execute_command(serverId, "timeout 3 bash -c 'cat < /dev/null > /dev/tcp/10.0.0.9/3306' && echo open || echo closed")
```

## 6.4 OOM / 内核 / 重启痕迹（可能需要 sudo）
```
ssh_execute_sudo(serverId, "dmesg -T | grep -iE 'oom|killed process' | tail -30")
ssh_execute_sudo(serverId, "journalctl -k --since '2 hours ago' | grep -i oom")
ssh_execute_sudo(serverId, "last reboot | head")
```

## 6.5 领域工具未启用时的替代
- 无 `docker_*`：`ssh_execute_command(serverId, "docker ps -a")` / `"docker logs --tail 200 <c>"` / `"docker stats --no-stream"`。
- 无 `service_*`：`"systemctl status <s> --no-pager"` / `"journalctl -u <s> -n 200 --no-pager"`；重启用 `ssh_execute_sudo(serverId, "systemctl restart <s>")`。
- 无 `java_*`：`"jstack -l <pid>"` / `"jcmd <pid> GC.heap_info"` / `"jstat -gcutil <pid> 1000 1"`。

## 6.6 远端配置修改（高风险，务必谨慎）
MCP 没有“编辑远端文件”工具。稳妥做法：
1. `ssh_download_file` 拉取配置到本地 → 本地修改；
2. `ssh_upload_file` 回传（需审批）；
3. `service_restart` / `ssh_execute_sudo systemctl reload`（需审批）；
4. 校验：`curl /actuator/health` 或看日志。
避免用 `sed -i` 之类直接改生产配置，除非别无选择且已有备份。

## 6.7 其它环境/编排（MCP 未内置）
- Kubernetes：`ssh_execute_command(serverId, "kubectl get pods -A -o wide")`、`"kubectl describe pod <p> -n <ns>"`、`"kubectl logs <p> -n <ns> --tail=200"`。
- Nginx：`"nginx -t"`、`"tail -n 200 /var/log/nginx/error.log"`。
- 消息队列/中间件：优先用其自带 CLI（`kafka-*`、`rabbitmqctl`）经 `ssh_execute_command`。
- 需要长期指标/追踪（Prometheus/APM）时，MCP 未覆盖，改用对应系统或经 ssh 拉取。

## 6.8 避免挂起 / 丢结果（重点避坑）
`ssh_execute_command` 走**无 TTY** 的 exec 通道：命令一直输出或不返回，就会拿不到结果直到超时。以下命令现在会被**前置拦截**并返回 `status=blocking_command`（附替代写法），被拦后按提示改写：

- **不要前台跑常驻进程**：`java -jar`、`./start.sh`、`npm start`、`gunicorn`/`uwsgi`(无 -d)、`docker run`(无 `-d`)、`docker compose up`(无 `-d`) 会一直占着前台。
  - 正确：`nohup <cmd> >/path/app.log 2>&1 &` 或 `setsid <cmd> ... &`，或做成 systemd 单元后 `systemctl start`、容器加 `-d`；
  - 启动后校验：`sleep 2; pgrep -af <name>; ss -ltnp | grep <port>; curl -s -m5 http://127.0.0.1:<port>/...`。
- **不要跟随输出**：`tail -f`→`tail -n 200`；`docker logs -f`→`docker logs --tail 200 <c>`；`journalctl -f`→`journalctl -n 200 --no-pager`；`kubectl logs -f`→`--tail=200`。
- **不要交互式命令**：`vi/vim/nano/less/more/top/htop/watch`、`bash -i`、`read`、`nc/telnet`；`ping` 必须加 `-c`。
- **`sudo`/`su` 走专用工具**：普通命令通道里的 `sudo`/`su` 会等密码挂起，用 `ssh_execute_sudo`（方式由服务器 `SudoType` 决定）。
- **多行 / 嵌套引号**：命令会被 `sh -c` 包装；避免复杂嵌套引号与 heredoc（易 `unexpected EOF`）。用 `;`/`&&` 串联；复杂脚本用 `ssh_upload_file` 传上去再 `bash /path/x.sh`。
- **退出码≠失败**：`grep`/`pgrep`/`diff`/`test`/`pkill` 返回非 0 往往是**正常语义**；判断看 `exitCode` + `output`/`error`，不要仅凭 `success=false` 认为命令没执行。
- **需要更长时间**：装包/重启等可传 `timeoutSeconds`（默认 60，最大 3600）；超时会返回 `status=timeout`。
- **不要结束通道**：避免 `exit`/`logout`、`kill` sshd、杀当前 shell 的父进程等——会关闭会话通道导致无结果。
