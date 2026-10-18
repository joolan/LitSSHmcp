// 流式聊天：/ws/sessions/{id} 协议（M3-3：C→S user/stop；S→C assistant_text/reasoning/
// tool/tool_result/memory_recall/done/error/busy）。助手输出只经 sanitize.renderMarkdown 落 DOM，
// 其余字段一律 textContent（模型/工具输出 = 不可信输入）。
import { api, wsUrl } from "./api.js";
import { renderMarkdown } from "./sanitize.js";

export class ChatController {
  constructor({ messagesEl, inputEl, sendBtn, stopBtn, onToast, onStatus }) {
    this.messagesEl = messagesEl;
    this.inputEl = inputEl;
    this.sendBtn = sendBtn;
    this.stopBtn = stopBtn;
    this.onToast = onToast;
    this.onStatus = onStatus;
    this.sessionId = null;
    this.ws = null;
    this.busy = false;
    this.assistantEl = null;
    this.assistantText = "";
    this.reasoningEl = null;
    this.reasoningText = "";
    this.lastToolEl = null;
    this.reconnectTimer = null;
    this.intentionalClose = false;
    this.hasConnected = false;
    this.persistedRows = 0;
    this.recovering = false;
  }

  async open(sessionId) {
    this.close();
    this.sessionId = sessionId;
    this.messagesEl.replaceChildren();
    try {
      const rows = await api(`/api/sessions/${sessionId}/messages`);
      this.persistedRows = rows.length;
      for (const row of rows) this.appendHistory(row);
    } catch (err) {
      this.onToast(`加载历史失败: ${err.message}`);
    }
    this.connect();
    // close() 已把输入框禁用（sessionId 为 null 时 setBusy 强制禁用），此处必须按
    // 已选会话重新启用——否则发不出第一条消息，也就永远等不到 done/error 解禁。
    this.setBusy(false);
  }

  close() {
    this.intentionalClose = true;
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
    this.sessionId = null;
    this.hasConnected = false;
    this.recovering = false;
    this.resetRun();
    this.setBusy(false);
    this.onStatus("");
  }

  connect() {
    const id = this.sessionId;
    if (id === null) return;
    this.intentionalClose = false;
    const ws = new WebSocket(wsUrl(`/ws/sessions/${id}`));
    this.ws = ws;

    ws.onmessage = (ev) => {
      if (ws !== this.ws) return; // 旧连接迟到的帧
      let frame;
      try {
        frame = JSON.parse(ev.data);
      } catch {
        return;
      }
      this.dispatch(frame);
    };

    ws.onopen = () => {
      if (ws !== this.ws) return;
      const reconnected = this.hasConnected;
      this.hasConnected = true;
      if (!this.busy) this.onStatus("就绪");
      // 重连后重放已落库历史：断线窗口内丢失的帧无法重发，至少恢复已完成内容
      if (reconnected && !this.busy) this.reloadHistory();
    };

    ws.onclose = () => {
      if (ws !== this.ws || this.intentionalClose) return;
      this.ws = null;
      if (this.busy) {
        // 回合仍在服务端执行：结果会在完成时落库 → 轮询历史直到恢复（最长 2 分钟）
        this.onStatus("连接断开，等待任务结果…");
        this.pollDroppedTurnResult();
      } else {
        this.onStatus("连接断开，重连中…");
        this.setBusy(false);
      }
      this.reconnectTimer = setTimeout(() => {
        this.reconnectTimer = null;
        if (this.sessionId !== null && this.sessionId === id) this.connect();
      }, 1500);
    };
  }

  /** 断线重放：按库内历史重建（仅已完成内容；流式中间帧不可恢复）。 */
  async reloadHistory() {
    if (this.sessionId === null) return;
    try {
      const rows = await api(`/api/sessions/${this.sessionId}/messages`);
      if (this.sessionId === null) return;
      this.persistedRows = rows.length;
      this.messagesEl.replaceChildren();
      this.resetRun();
      for (const row of rows) this.appendHistory(row);
      this.scrollDown();
    } catch {
      /* 忽略：WS 推送/下次重连仍可恢复 */
    }
  }

  /** 断线时回合未结束：轮询库内消息，直到本轮结果落库（Web 在回合结束才写 assistant 消息）。 */
  async pollDroppedTurnResult() {
    if (this.recovering) return;
    this.recovering = true;
    const deadline = Date.now() + 120000;
    try {
      while (Date.now() < deadline && this.sessionId !== null) {
        await new Promise((r) => setTimeout(r, 3000));
        if (this.sessionId === null) return;
        try {
          const rows = await api(`/api/sessions/${this.sessionId}/messages`);
          if (rows.length > this.persistedRows && rows[rows.length - 1]?.role === "assistant") {
            this.persistedRows = rows.length;
            this.messagesEl.replaceChildren();
            this.resetRun();
            for (const row of rows) this.appendHistory(row);
            this.scrollDown();
            this.setBusy(false);
            this.onStatus("已恢复任务结果");
            return;
          }
        } catch {
          /* 下一轮再试 */
        }
      }
      if (this.sessionId !== null) {
        this.setBusy(false);
        this.onStatus("就绪");
        this.onToast("断线期间的任务结果未能自动恢复，可重新打开会话查看");
      }
    } finally {
      this.recovering = false;
    }
  }

  dispatch(frame) {
    switch (frame.type) {
      case "assistant_text": {
        const md = this.ensureAssistant();
        this.assistantText += frame.text || "";
        md.innerHTML = renderMarkdown(this.assistantText);
        this.scrollDown();
        break;
      }
      case "reasoning":
        this.ensureReasoning().textContent += frame.text || "";
        this.scrollDown();
        break;
      case "tool":
        this.appendToolCard(frame);
        this.onStatus("执行工具中…");
        this.scrollDown();
        break;
      case "tool_result":
        this.finishToolCard(frame);
        this.scrollDown();
        break;
      case "memory_recall":
        this.appendMemory(frame.text || "");
        this.scrollDown();
        break;
      case "done":
        this.setBusy(false);
        this.onStatus(frame.stopped ? "已停止" : "就绪");
        this.resetRun();
        break;
      case "error":
        this.onToast(frame.message || "发生错误");
        this.setBusy(false);
        this.onStatus("就绪");
        this.resetRun();
        break;
      case "busy":
        this.onToast(frame.message || "上一轮仍在进行中");
        this.setBusy(false);
        this.onStatus("就绪");
        break;
      default:
        break;
    }
  }

  send(text) {
    if (this.sessionId === null) return;
    if (this.ws === null || this.ws.readyState !== WebSocket.OPEN) {
      this.onToast("连接未就绪，请稍候");
      this.connect();
      return;
    }
    const bubble = document.createElement("div");
    bubble.className = "bubble user";
    bubble.textContent = text;
    this.messagesEl.append(bubble);
    this.resetRun();
    this.ws.send(JSON.stringify({ type: "user", text }));
    this.setBusy(true);
    this.onStatus("执行中…");
    this.scrollDown();
  }

  stop() {
    if (this.ws !== null && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ type: "stop" }));
      this.onStatus("停止中…");
    }
  }

  appendHistory(row) {
    if (row.role === "user") {
      const bubble = document.createElement("div");
      bubble.className = "bubble user";
      bubble.textContent = row.content || "";
      this.messagesEl.append(bubble);
    } else {
      const bubble = document.createElement("div");
      bubble.className = "bubble assistant";
      const md = document.createElement("div");
      md.innerHTML = renderMarkdown(row.content || "");
      bubble.append(md);
      this.messagesEl.append(bubble);
    }
    this.scrollDown();
  }

  ensureAssistant() {
    if (this.assistantEl === null) {
      const bubble = document.createElement("div");
      bubble.className = "bubble assistant";
      const md = document.createElement("div");
      bubble.append(md);
      this.messagesEl.append(bubble);
      this.assistantEl = bubble;
      this.assistantText = "";
    }
    return this.assistantEl.querySelector("div");
  }

  ensureReasoning() {
    if (this.reasoningEl === null) {
      const details = document.createElement("details");
      details.className = "reasoning";
      const summary = document.createElement("summary");
      summary.textContent = "思考过程";
      const body = document.createElement("div");
      body.className = "reasoning-body";
      details.append(summary, body);
      this.messagesEl.append(details);
      this.reasoningEl = body;
      this.reasoningText = "";
    }
    return this.reasoningEl;
  }

  appendToolCard(frame) {
    const card = document.createElement("div");
    card.className = "tool running";
    const head = document.createElement("div");
    head.className = "tool-head";
    const name = document.createElement("span");
    name.className = "tool-name";
    name.textContent = frame.tool || "tool";
    const status = document.createElement("span");
    status.className = "tool-status";
    status.textContent = "运行中…";
    head.append(name, status);
    const pre = document.createElement("pre");
    pre.className = "tool-args";
    let args = frame.args || "";
    try {
      args = JSON.stringify(JSON.parse(args), null, 2);
    } catch {
      /* 保持原文 */
    }
    pre.textContent = args;
    card.append(head, pre);
    this.messagesEl.append(card);
    this.lastToolEl = card;
  }

  finishToolCard(frame) {
    const card = this.lastToolEl;
    if (card === null) return;
    card.classList.remove("running");
    card.classList.add(frame.ok ? "ok" : "fail");
    const status = card.querySelector(".tool-status");
    const ms = typeof frame.ms === "number" ? ` · ${frame.ms}ms` : "";
    status.textContent = frame.ok ? `完成${ms}` : `失败${ms}`;
    this.lastToolEl = null;
  }

  appendMemory(text) {
    const el = document.createElement("div");
    el.className = "memory";
    el.textContent = `记忆召回: ${text}`;
    this.messagesEl.append(el);
  }

  resetRun() {
    this.assistantEl = null;
    this.assistantText = "";
    this.reasoningEl = null;
    this.reasoningText = "";
    this.lastToolEl = null;
  }

  setBusy(busy) {
    this.busy = busy;
    this.inputEl.disabled = busy;
    this.sendBtn.disabled = busy;
    this.stopBtn.disabled = !busy;
    if (this.sessionId === null) {
      this.inputEl.disabled = true;
      this.sendBtn.disabled = true;
      this.stopBtn.disabled = true;
    }
  }

  scrollDown() {
    this.messagesEl.scrollTop = this.messagesEl.scrollHeight;
  }
}
