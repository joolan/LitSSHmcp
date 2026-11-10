# 配置参考

配置文件位置：`%APPDATA%\LitSSH\config.json`

```json
{
  "servers": [
    {
      "id": "my-server",
      "name": "我的服务器",
      "host": "192.0.2.10",
      "port": 22,
      "username": "root",
      "authType": "Password",
      "password": "your-password",
      "sudoType": "CurrentUser",
      "sudoPassword": "your-sudo-password"
    }
  ],
  "dataSources": [
    {
      "id": "mysql-order-01",
      "name": "订单库",
      "type": "mysql",
      "host": "192.0.2.11",
      "port": 3306,
      "username": "order_app",
      "password": "your-db-password",
      "defaultDatabase": "orders",
      "accessMode": "sshTunnel",
      "tunnelServerId": "my-server",
      "sslMode": "Preferred"
    },
    {
      "id": "redis-cache-01",
      "name": "缓存",
      "type": "redis",
      "host": "192.0.2.11",
      "port": 6379,
      "username": "",
      "password": "your-redis-password",
      "defaultDatabase": "0",
      "accessMode": "sshTunnel",
      "tunnelServerId": "my-server"
    },
    {
      "id": "pg-report-01",
      "name": "报表库",
      "type": "postgres",
      "host": "192.0.2.11",
      "port": 5432,
      "username": "report_app",
      "password": "your-pg-password",
      "defaultDatabase": "report",
      "accessMode": "sshTunnel",
      "tunnelServerId": "my-server",
      "readOnly": true,
      "maxRows": 500,
      "timeoutSeconds": 30,
      "writeApproval": "Always"
    }
  ],
  "applications": [
    { "id": "order-service", "name": "订单服务", "type": "java", "port": 8080, "host": "192.0.2.10" },
    { "id": "order-worker", "name": "订单Worker", "type": "docker", "containerName": "order-worker", "host": "192.0.2.10" }
  ],
  "relations": [
    { "from": "app:order-service", "to": "ssh:my-server", "type": "runsOn" },
    { "from": "app:order-worker", "to": "ssh:my-server", "type": "runsOn" },
    { "from": "app:order-service", "to": "ds:mysql-order-01", "type": "connectsTo" },
    { "from": "ssh:my-server", "to": "ds:mysql-order-01", "type": "canAccess" }
  ],
  "tools": {
    "enabledGroups": ["ssh", "command", "datasource", "mysql", "postgres", "redis", "docker", "service", "log", "java", "topology", "app", "guide"]
  },
  "security": {
    "enabled": true,
    "commandFilter": {
      "blockedCommands": ["rm -rf /", "mkfs", "dd if=/dev/zero"],
      "sensitiveCommands": ["rm ", "chmod", "reboot", "shutdown"],
      "sensitivePatterns": ["\\brm\\b", "\\bchmod\\b"]
    },
    "sqlFilter": {
      "blockedPatterns": ["\\btruncate\\b", "\\bgrant\\b"],
      "sensitivePatterns": ["\\binsert\\b", "\\bupdate\\b", "\\bdelete\\b"]
    },
    "fileTransfer": {
      "enabled": true,
      "requireApproval": true,
      "maxFileSizeBytes": 104857600,
      "allowedLocalPaths": ["C:\\Users\\you\\Desktop"],
      "allowedRemotePaths": ["/home", "/tmp", "/var/log"]
    },
    "sshHostKey": { "mode": "tofu" },
    "discovery": { "allowedSearchPaths": ["/opt", "/home", "/srv", "/app", "/data", "/etc/nginx"], "useSudo": false },
    "limits": { "maxConcurrentPerTarget": 3, "maxCallsPerMinutePerTarget": 60 },
    "audit": { "storeSqlText": true, "maskLiterals": false, "retentionDays": 90 },
    "masking": { "rules": [ { "column": "phone|mobile", "mode": "phone" } ] },
    "approval": { "style": "process", "mode": "manual", "channels": ["desktop", "cli"], "timeoutSeconds": 45, "topMost": true }
  },
  "connectionPool": { "enabled": true, "idleTimeoutSeconds": 300, "keepAliveSeconds": 30, "connectTimeoutSeconds": 20, "maxPerServer": 1 },
  "ui": { "theme": "system", "accent": "" }
}
```

> 所有明文密码在保存 / 加载时会自动迁移为 **DPAPI 密文**（`enc:` 前缀），仅当前 Windows 用户可解密。访问模式、拓扑关系类型、审批弹窗样式等含义详见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 近期新增字段

```json
{
  "schemaVersion": 7,
  "snapshot": {
    "useSudo": true,
    "retentionPerServer": 30,
    "timeoutSeconds": 180,
    "minRefreshIntervalSeconds": 60,
    "collectAccounts": true,
    "collectAuthorizedKeys": true,
    "collectLoginHistory": true,
    "storeEffectiveConfig": false
  },
  "agent": {
    "retentionMaxMessages": 2000
  }
}
```

- `snapshot.collectAccounts` / `collectAuthorizedKeys` / `collectLoginHistory`：账号安全维度与公钥/登录历史采集开关。
- `snapshot.storeEffectiveConfig`：默认 `false`，完整 `nginx -T` 存入 `snapshots.db` 的 `NginxConfigs` 表，`detail="full"` 时回填。
- `agent.retentionMaxMessages`：默认 2000；按整轮裁剪，被裁消息归档到 `agent_messages_deleted`，不会单独丢掉 user 提示词。
