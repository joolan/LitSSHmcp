---
name: ssl-cert-expiry
description: HTTPS 证书即将/已过期或证书链问题排查。适用于「证书过期」「NET::ERR_CERT」「https 报错」「证书不匹配」。
appliesTo: [ssh, command, topology]
---

# TLS/HTTPS 证书排查

## 步骤
1. **看有效配置与域名**：优先 `ssh_snapshot_get section="nginx_tls"`，拿到 `domains`、每个域名关联的证书路径与**到期时间**（该维度用 nginx 可执行文件跑 `nginx -T` 取全量配置）。
   图谱里若已有证书/域名关系，先用 `topology_query(action="search")` 找 `cert:`/`domain:` 节点，再用 `topology_query(action="impact", assetId="cert:<id>")` 反查受影响域名、`topology_query(action="explain")` 核对关系来源（图谱未覆盖时以快照为准）。
2. **直接探测证书**（任选域名）：
   `ssh_execute_command "echo | openssl s_client -connect <host>:443 -servername <domain> 2>/dev/null | openssl x509 -noout -subject -issuer -dates -ext subjectAltName"`
   看 `notAfter`（到期）、`subject/SAN`（域名是否匹配）。
3. **证书链**：`openssl s_client -connect <host>:443 -servername <domain> -showcerts` 看是否缺中间证书。
4. **文件层面**：`ssh_execute_sudo "ls -l <certfile>"` 看文件权限/属主；确认未换证书但未 reload。

## 判定与处置
- 快到期 → 提前续期（Let's Encrypt `certbot renew` 或商业证书替换），续期后 `nginx -s reload`（`ssh_execute_sudo`，需确认）。
- 域名不匹配（SAN 缺当前域名）→ 重新签发含全部域名的证书。
- 链不完整 → 配置 `ssl_certificate` 指向含中间证书的 fullchain。
- 变更后用第 2 步复测 `notAfter`/SAN。

## 注意
- 续期/替换证书属变更操作，需用户明确要求；自动续期先确认 cron/timer 是否正常。
- 不要忽略 `host_key_mismatch`（可能与证书问题混淆，属 SSH 层安全事件）。

## 深入（按需 skill_read）
证书周期巡检 runbook：`references/inspection-runbook.md`（批量探测、到期阈值分级、续期计划与复测）。
