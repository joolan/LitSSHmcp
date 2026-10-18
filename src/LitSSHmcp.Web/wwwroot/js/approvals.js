// 审批卡片：/ws/approvals（M3-5 协议：S→C approval_snapshot/approval_request/approval_closed；
// 决策走 REST POST /api/approvals/{id}，吃 CSRF）。卡片字段一律 textContent（命令串不可信）。
// 展示为**固定悬浮面板**（不随聊天滚动）；另以 REST 对账（定时/决策后/WS 重连）兜底漏推，
// 避免"第二次审批不出现、只能刷新页面"。
import { api, wsUrl } from "./api.js";

export class ApprovalsPanel {
  constructor({ stripEl, overlayEl, badgeEl, countEl, onToast, reconcileIntervalMs = 5000 }) {
    this.stripEl = stripEl;
    this.overlayEl = overlayEl;
    this.badgeEl = badgeEl;
    this.countEl = countEl;
    this.onToast = onToast;
    this.reconcileIntervalMs = reconcileIntervalMs;
    this.pending = new Map(); // id -> item
    this.ws = null;
    this.pingTimer = null;
    this.reconnectTimer = null;
    this.reconcileTimer = null;
    this.collapsed = false;
    this.stopped = false;
  }

  start() {
    this.stopped = false;
    this.connect();
    // REST 对账：即使 WS 漏帧/重连窗口，也能在 5s 内恢复待决列表
    this.reconcileTimer = setInterval(() => this.reconcile(), this.reconcileIntervalMs);
    this.reconcile();
  }

  stop() {
    this.stopped = true;
    this.clearTimers();
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

  clearTimers() {
    if (this.pingTimer !== null) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.reconcileTimer !== null) {
      clearInterval(this.reconcileTimer);
      this.reconcileTimer = null;
    }
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
      this.handleFrame(frame);
    };

    ws.onclose = () => {
      if (ws !== this.ws || this.stopped) return;
      this.ws = null;
      if (this.pingTimer !== null) {
        clearInterval(this.pingTimer);
        this.pingTimer = null;
      }
      // 断线期间可能漏推：立即对账一次，再按退避重连
      this.reconcile();
      this.reconnectTimer = setTimeout(() => {
        this.reconnectTimer = null;
        this.connect();
      }, 2000);
    };
  }

  /** WS 帧处理（抽成方法便于 node 测试直调）。 */
  handleFrame(frame) {
    switch (frame.type) {
      case "approval_snapshot":
        this.pending = new Map((frame.items || []).map((item) => [item.id, item]));
        this.render();
        break;
      case "approval_request":
        if (frame.item && frame.item.id) {
          this.pending.set(frame.item.id, frame.item);
          // 新审批到达时自动展开（用户可能已收起面板）
          this.collapsed = false;
          this.render();
        }
        break;
      case "approval_closed":
        if (frame.id && this.pending.delete(frame.id)) this.render();
        break;
      default:
        break; // pong 等
    }
  }

  /** REST 全量对账：以服务端待决列表为准（漏推自愈；不触碰正在提交的项）。 */
  async reconcile() {
    if (this.stopped) return;
    try {
      const data = await api("/api/approvals");
      if (this.stopped) return;
      const items = data?.items || [];
      const next = new Map();
      for (const item of items) {
        const existing = this.pending.get(item.id);
        next.set(item.id, existing ? { ...item, deciding: existing.deciding, decidingLabel: existing.decidingLabel } : item);
      }
      this.pending = next;
      this.render();
    } catch {
      /* 对账失败忽略（下一轮/WS 会再试） */
    }
  }

  setCollapsed(collapsed) {
    this.collapsed = collapsed;
    this.render();
  }

  render() {
    const cards = [...this.pending.values()].map((item) => this.buildCard(item));
    this.stripEl.replaceChildren(...cards);
    const count = cards.length;
    if (this.countEl) this.countEl.textContent = String(count);
    if (this.badgeEl) this.badgeEl.hidden = count === 0;
    if (this.overlayEl) this.overlayEl.hidden = count === 0 || this.collapsed;
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
      item.deciding = false;
      this.render();
      // 决策后立即对账：后面的审批可能已经生成（不再依赖单次 WS 推送）
      await this.reconcile();
    } catch (err) {
      this.pending.delete(item.id);
      this.render();
      if (err.status === 404) {
        this.onToast("审批已失效（超时或已在别处处理）");
      } else {
        this.onToast(`提交失败: ${err.message}`);
      }
      await this.reconcile();
    }
  }
}
