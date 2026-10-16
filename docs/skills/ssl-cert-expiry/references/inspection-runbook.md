# TLS 证书周期巡检 runbook（按需读取）

适用：例行巡检批量检查域名证书到期/域名匹配/证书链，输出续期计划。只读探测为主；续期/替换属变更，另走确认流程。

## 1. 批量采集（每台主机一轮）
1. nginx 主机优先：`ssh_snapshot_get section="nginx_tls"`——直接得到各域名证书路径与**到期时间**（覆盖 `nginx -T` 全量配置里的 server_name/SNI）。
2. 无 nginx 或非 nginx：对每个关键域名探测：
   `ssh_execute_command "echo | openssl s_client -connect <host>:443 -servername <domain> 2>/dev/null | openssl x509 -noout -subject -issuer -dates -ext subjectAltName"`
   记录 `notAfter`、`issuer`、SAN 是否含该域名。
3. 链完整性抽查（高价值域名）：`openssl s_client ... -showcerts` 看证书数量（缺中间证书会导致部分客户端失败）。
4. 文件层（配置指向的证书）：`ssh_execute_sudo "ls -l <certfile>; openssl x509 -in <certfile> -noout -dates"`——磁盘文件到期时间应与线上一致（防换文件未 reload）。

## 2. 判定阈值
| 级别 | 条件 | 动作 |
|---|---|---|
| 正常 | 剩余 ≥30 天且 SAN 匹配、链完整 | 记录 |
| 关注 | 剩余 8~30 天 | 列入近期续期计划 |
| 风险 | 剩余 ≤7 天或域名不匹配/链不完整 | 立即报告，建议本周期内处置 |
| 紧急 | 已过期或线上探测失败 | 立即升级；对照 ssl-cert-expiry 处置流程 |

## 3. 巡检报告模板
- 域名 / 证书 subject / issuer / 到期日 / 剩余天数 / SAN 匹配 / 链完整 / 来源（nginx_tls 或在线探测）
- 自动续期机制状态（certbot timer / ACME 客户端最近一次 renew 日志）
- 建议：续期动作、涉及 reload 的服务、回滚点（旧证书备份路径）

## 4. 巡检后
1. 「关注」及以上项写入记忆（域名、到期日、续期建议），下轮巡检自动召回对比。
2. 续期实施：用户确认后 `certbot renew` / 替换文件 + `nginx -s reload`（`ssh_execute_sudo` 审批），完成后用在线探测复测 `notAfter` 与 SAN。

## 注意
- SNI 必填 `-servername`，否则默认站点证书会造成误判。
- SAN 覆盖检查包含裸域与通配符匹配规则，勿只看 subject CN。
- 不要忽略 `host_key_mismatch`（SSH 层安全事件，与证书问题分开报告）。
