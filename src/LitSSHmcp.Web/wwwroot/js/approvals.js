// 审批卡片：/ws/approvals（M3-5 协议：S→C approval_snapshot/approval_request/approval_closed；
// 决策走 REST POST /api/approvals/{id}，吃 CSRF）。卡片字段一律 textContent（命令串不可信）。
import { api, wsUrl } from "./api.js";

export class ApprovalsPanel {
  constructor({ stripEl, onToast }) {
    this.stripEl = stripEl;
    this.onToast = onToast;
    this.pending = new Map(); // id -> item
    this.ws = null;
    this.pingTimer = null;
    this.reconnectTimer = null;
    this.stopped = false;
  }

  start() {
    this.stopped = false;
    this.connect();
  }

  stop() {
    this.stopped = true;
    if (this.pingTimer !== null) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.ws !== null) {
      try {
        this.ws.close();
      } catch {
        /* ignore */
      }
      this.ws = null;
    }
    this.pending.clear();
    this.render();
  }

  connect() {
    if (this.stopped) return;
    const ws = new WebSocket(wsUrl("/ws/approvals"));
    this.ws = ws;

    ws.onopen = () => {
      if (ws !== this.ws) return;
      this.pingTimer = setInterval(() => {
        if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: "ping" }));
      }, 25000);
    };

    ws.onmessage = (ev) => {
      if (ws !== this.ws) return;
      let frame;
      try {
        frame = JSON.parse(ev.data);
      } catch {
        return;
      }
      switch (frame.type) {
        case "approval_snapshot":
          this.pending = new Map((frame.items || []).map((item) => [item.id, item]));
          this.render();
          break;
        case "approval_request":
          if (frame.item && frame.item.id) {
            this.pending.set(frame.item.id, frame.item);
            this.render();
          }
          break;
        case "approval_closed":
          if (frame.id && this.pending.delete(frame.id)) this.render();
          break;
        default:
          break; // pong 等
      }
    };

    ws.onclose = () => {
      if (ws !== this.ws || this.stopped) return;
      this.ws = null;
      if (this.pingTimer !== null) {
        clearInterval(this.pingTimer);
        this.pingTimer = null;
      }
      this.reconnectTimer = setTimeout(() => {
        this.reconnectTimer = null;
        this.connect();
      }, 2000);
    };
  }

  render() {
    const cards = [...this.pending.values()].map((item) => this.buildCard(item));
    this.stripEl.replaceChildren(...cards);
    this.stripEl.hidden = cards.length === 0;
  }

  buildCard(item) {
    const card = document.createElement("div");
    card.className = "appr";

    const head = document.createElement("div");
    head.className = "appr-head";
    const title = document.createElement("span");
    title.textContent = `审批请求 · ${item.server} · ${item.operation}`;
    const expiry = document.createElement("span");
    expiry.className = "appr-expiry";
    expiry.textContent = item.expiresAt ? `截止 ${new Date(item.expiresAt).toLocaleTimeString()}` : "";
    head.append(title, expiry);

    const cmd = document.createElement("pre");
    cmd.className = "appr-cmd";
    cmd.textContent = item.command || "";

    const actions = document.createElement("div");
    actions.className = "appr-actions";
    const allow = document.createElement("button");
    allow.className = "primary";
    allow.textContent = "允许";
    const deny = document.createElement("button");
    deny.className = "danger";
    deny.textContent = "拒绝";
    actions.append(allow, deny);

    if (item.deciding) {
      allow.disabled = true;
      deny.disabled = true;
      actions.append(document.createTextNode(item.decidingLabel || "提交中…"));
    } else {
      allow.addEventListener("click", () => this.decide(item, true, allow, deny));
      deny.addEventListener("click", () => this.decide(item, false, allow, deny));
    }

    card.append(head, cmd, actions);
    return card;
  }

  async decide(item, approved, ...buttons) {
    item.deciding = true;
    item.decidingLabel = approved ? "允许中…" : "拒绝中…";
    this.render();
    try {
      await api(`/api/approvals/${encodeURIComponent(item.id)}`, {
        method: "POST",
        body: { approved },
      });
      // 成功后等 approval_closed（MCP 消化/清理）移除卡片
      item.deciding = false;
      this.render();
    } catch (err) {
      this.pending.delete(item.id);
      this.render();
      if (err.status === 404) {
        this.onToast("审批已失效（超时或已在别处处理）");
      } else {
        this.onToast(`提交失败: ${err.message}`);
      }
    }
  }
}
