// 流式聊天：/ws/sessions/{id} 协议（C→S user/stop；S→C assistant_text/reasoning/tool/
// tool_result/memory_recall/asset_graph_context/done/error/busy）。助手输出只经 sanitize.renderMarkdown 落 DOM，
// 其余字段一律 textContent（模型/工具输出 = 不可信输入）。
// 支持图片附件（粘贴/选择/拖拽，≤5 张、单张 ≤10MB）：先 REST 上传到会话目录，再随 user 帧回传。
import { api, downloadUrl, upload, wsUrl } from "./api.js";
import { renderMarkdown } from "./sanitize.js";

const MAX_IMAGES = 5;
const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
const IMAGE_TYPE_RE = /^image\/(png|jpe?g|webp|gif)$/i;

/** 解析历史里的工具轨迹行（AgentTranscript JSON：Tool/Args/Result/Ok/Ms/IsInfo，兼容 camelCase）。 */
export function parseToolRow(content) {
  try {
    const raw = JSON.parse(String(content ?? ""));
    if (!raw || typeof raw !== "object") return null;
    const tool = raw.Tool ?? raw.tool;
    if (!tool) return null;
    return {
      tool: String(tool),
      args: raw.Args ?? raw.args ?? "",
      result: raw.Result ?? raw.result ?? "",
      ok: raw.Ok ?? raw.ok ?? true,
      ms: raw.Ms ?? raw.ms ?? 0,
      isInfo: (raw.IsInfo ?? raw.isInfo) === true,
    };
  } catch {
    return null;
  }
}

function prettyJson(text) {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return String(text ?? "");
  }
}

/**
 * 解析用户消息里的图片标记（与桌面端一致：🖼 图片: 名称 [相对路径]）。
 * 返回 { text: 去掉标记后的展示文本, images: 相对路径数组 }。
 */
export function parseUserContent(content) {
  const images = [];
  const lines = [];
  for (const raw of String(content ?? "").replace(/\r\n/g, "\n").split("\n")) {
    const line = raw.trim();
    if (line.startsWith("🖼")) {
      const open = line.indexOf("[");
      const close = line.lastIndexOf("]");
      if (open >= 0 && close > open) images.push(line.slice(open + 1, close).trim());
      continue;
    }
    lines.push(raw);
  }
  return { text: lines.join("\n").trim(), images };
}

export class ChatController {
  constructor({ messagesEl, inputEl, sendBtn, stopBtn, attachBtn, fileInput, chipsEl, toBottomEl, loadOlderBtn, onToast, onStatus, onConnState }) {
    this.messagesEl = messagesEl;
    this.inputEl = inputEl;
    this.sendBtn = sendBtn;
    this.stopBtn = stopBtn;
    this.attachBtn = attachBtn;
    this.fileInput = fileInput;
    this.chipsEl = chipsEl;
    this.toBottomEl = toBottomEl;
    this.loadOlderBtn = loadOlderBtn;
    this.onToast = onToast;
    this.onStatus = onStatus;
    this.onConnState = onConnState;
    this.sessionId = null;
    this.ws = null;
    this.busy = false;
    this.uploading = false;
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
    this.files = [];
    this.renderQueued = false;
    this.stick = true;
    this.pageSize = 120;
    this.hasMore = false;
    this.oldestId = null;
    this.lastAssistantContent = "";

    // 只有接近底部时才跟随滚动（用户向上翻历史时不打断）
    this.messagesEl.addEventListener?.("scroll", () => {
      this.stick = this.isNearBottom();
      if (this.toBottomEl) this.toBottomEl.hidden = this.stick;
    });
    this.toBottomEl?.addEventListener?.("click", () => {
      this.stick = true;
      this.scrollDown(true);
      if (this.toBottomEl) this.toBottomEl.hidden = true;
    });
    this.loadOlderBtn?.addEventListener?.("click", () => {
      this.loadOlder().catch((err) => this.onToast(err.message));
    });
  }

  async open(sessionId) {
    this.close();
    this.sessionId = sessionId;
    this.messagesEl.replaceChildren();
    await this.loadHistory();
    this.connect();
    // close() 已把输入框禁用（sessionId 为 null 时 setBusy 强制禁用），此处必须按
    // 已选会话重新启用——否则发不出第一条消息，也就永远等不到 done/error 解禁。
    this.setBusy(false);
    this.onStatus("就绪");
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
    this.hasMore = false;
    this.oldestId = null;
    this.lastAssistantContent = "";
    if (this.loadOlderBtn) this.loadOlderBtn.hidden = true;
    this.clearFiles();
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
      this.onConnState?.("ok");
      if (!this.busy) this.onStatus("就绪");
      // 重连后重放已落库历史：断线窗口内丢失的帧无法重发，至少恢复已完成内容
      if (reconnected && !this.busy) this.loadHistory();
    };

    ws.onclose = () => {
      if (ws !== this.ws || this.intentionalClose) return;
      this.ws = null;
      this.onConnState?.("down");
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

  dispatch(frame) {
    switch (frame.type) {
      case "assistant_text": {
        const md = this.ensureAssistant();
        this.assistantText += frame.text || "";
        this.queueAssistantRender(md);
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
        this.appendInfo("记忆召回", frame.text || "");
        this.scrollDown();
        break;
      case "asset_graph_context":
        this.appendInfo("资产图谱", frame.text || "");
        this.scrollDown();
        break;
      case "done":
        this.flushAssistantRender();
        this.setBusy(false);
        this.onStatus(frame.stopped ? "已停止" : "就绪");
        this.resetRun();
        // 以落库内容为准重放（权威视图）：流式帧可能丢尾，导致"结论内容不全"；
        // 服务端在 done 之前已把 assistant 文本落库，因此这里重建一定完整。
        if (!frame.stopped) this.reloadFromStore();
        break;
      case "error":
        this.flushAssistantRender();
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

  async send(text) {
    if (this.sessionId === null || this.busy || this.uploading) return false;
    if (this.ws === null || this.ws.readyState !== WebSocket.OPEN) {
      this.onToast("连接未就绪，请稍候");
      this.connect();
      return false;
    }
    const trimmed = String(text ?? "").trim();
    if (!trimmed && this.files.length === 0) return false;

    const files = this.files.slice();
    const attachments = [];
    if (files.length > 0) {
      this.uploading = true;
      this.onStatus(`上传图片（${files.length}）…`);
      try {
        for (const file of files) {
          const uploaded = await upload(`/api/sessions/${this.sessionId}/attachments`, file);
          attachments.push({
            path: uploaded.path,
            contentType: uploaded.contentType,
            name: uploaded.name,
          });
        }
      } catch (err) {
        this.onToast(`图片上传失败: ${err.message}`);
        this.uploading = false;
        this.onStatus("就绪");
        return false;
      }
      this.uploading = false;
    }

    this.appendUser(trimmed, files.map((f) => URL.createObjectURL(f)));
    this.clearFiles();
    this.resetRun();
    this.ws.send(JSON.stringify({ type: "user", text: trimmed, attachments }));
    this.setBusy(true);
    this.onStatus("执行中…");
    this.scrollDown(true);
    return true;
  }

  stop() {
    if (this.ws !== null && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ type: "stop" }));
      this.onStatus("停止中…");
    }
  }

  // ---- 附件 ----

  addFiles(fileList) {
    const incoming = Array.from(fileList ?? []);
    for (const file of incoming) {
      if (this.files.length >= MAX_IMAGES) {
        this.onToast(`最多 ${MAX_IMAGES} 张图片`);
        break;
      }
      if (!IMAGE_TYPE_RE.test(file.type || "")) {
        this.onToast("仅支持 png / jpeg / webp / gif 图片");
        continue;
      }
      if (file.size > MAX_IMAGE_BYTES) {
        this.onToast("单张图片不能超过 10MB");
        continue;
      }
      this.files.push(file);
    }
    this.renderChips();
  }

  removeFile(index) {
    this.files.splice(index, 1);
    this.renderChips();
  }

  clearFiles() {
    this.files = [];
    this.renderChips();
  }

  renderChips() {
    if (!this.chipsEl) return;
    if (this.files.length === 0) {
      this.chipsEl.replaceChildren();
      this.chipsEl.hidden = true;
      return;
    }
    const chips = this.files.map((file, index) => {
      const chip = document.createElement("span");
      chip.className = "chip";
      const name = document.createElement("span");
      name.textContent = file.name || `图片 ${index + 1}`;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "chip-x";
      remove.textContent = "×";
      remove.title = "移除";
      remove.addEventListener("click", () => this.removeFile(index));
      chip.append(name, remove);
      return chip;
    });
    this.chipsEl.replaceChildren(...chips);
    this.chipsEl.hidden = false;
  }

  // ---- 历史（分页 + 含工具轨迹行） ----

  async loadHistory() {
    if (this.sessionId === null) return;
    try {
      const rows = await api(`/api/sessions/${this.sessionId}/messages?limit=${this.pageSize}`);
      if (this.sessionId === null) return;
      this.messagesEl.replaceChildren();
      this.resetRun();
      this.oldestId = null;
      this.lastAssistantContent = "";
      for (const row of rows) this.appendHistoryRow(row);
      this.persistedRows = rows.length;
      this.updatePaging(rows);
      this.scrollDown(true);
    } catch (err) {
      this.onToast(`加载历史失败: ${err.message}`);
    }
  }

  /** 记录分页状态（最早 Id / 是否还有更早）。 */
  updatePaging(rows) {
    if (rows.length > 0) {
      const firstId = rows[0].id;
      this.oldestId = this.oldestId === null ? firstId : Math.min(this.oldestId, firstId);
    }
    this.hasMore = rows.length >= this.pageSize;
    if (this.loadOlderBtn) this.loadOlderBtn.hidden = !this.hasMore;
  }

  /** 向前翻页：把更早的消息插到顶部，并保持当前滚动位置不跳动。 */
  async loadOlder() {
    if (this.sessionId === null || this.oldestId === null || !this.hasMore) return;
    const sessionId = this.sessionId;
    const beforeId = this.oldestId;
    const rows = await api(`/api/sessions/${sessionId}/messages?beforeId=${beforeId}&limit=${this.pageSize}`);
    if (this.sessionId !== sessionId) return;
    if (rows.length === 0) {
      this.hasMore = false;
      if (this.loadOlderBtn) this.loadOlderBtn.hidden = true;
      return;
    }

    const container = this.messagesEl;
    const prevHeight = container.scrollHeight ?? 0;
    const fragment = typeof document.createDocumentFragment === "function"
      ? document.createDocumentFragment()
      : document.createElement("div");
    for (const row of rows) this.appendHistoryRow(row, fragment);
    if (typeof container.prepend === "function") container.prepend(fragment);
    else container.append(fragment);

    this.oldestId = rows[0]?.id ?? this.oldestId;
    this.hasMore = rows.length >= this.pageSize;
    if (this.loadOlderBtn) this.loadOlderBtn.hidden = !this.hasMore;
    container.scrollTop = (container.scrollTop ?? 0) + ((container.scrollHeight ?? 0) - prevHeight);
  }

  appendHistoryRow(row, container = this.messagesEl) {
    switch (row.role) {
      case "user": {
        const parsed = parseUserContent(row.content);
        const urls = parsed.images
          .filter((p) => p.startsWith("attachments/") && !p.includes(".."))
          .map((p) => downloadUrl(this.sessionId, p));
        this.appendUser(parsed.text, urls, container);
        break;
      }
      case "tool":
        this.appendToolHistory(row.content, container);
        break;
      case "reasoning":
        this.appendReasoningHistory(row.content, container);
        break;
      default: {
        const text = row.content || "";
        if (container === this.messagesEl) this.lastAssistantContent = String(text);
        this.appendAssistant(text, container);
        break;
      }
    }
  }

  appendUser(text, imageUrls = [], container = this.messagesEl) {
    const bubble = document.createElement("div");
    bubble.className = "bubble user";
    const label = document.createElement("div");
    label.className = "bubble-text";
    label.textContent = text;
    bubble.append(label);
    if (imageUrls.length > 0) {
      const gallery = document.createElement("div");
      gallery.className = "bubble-images";
      for (const url of imageUrls) {
        const img = document.createElement("img");
        img.src = url;
        img.alt = "附图";
        img.addEventListener?.("load", () => URL.revokeObjectURL?.(url));
        img.addEventListener?.("error", () => URL.revokeObjectURL?.(url));
        gallery.append(img);
      }
      bubble.append(gallery);
    }
    container.append(bubble);
    if (container === this.messagesEl) this.scrollDown();
  }

  appendAssistant(text, container = this.messagesEl) {
    if (!text) return;
    const bubble = document.createElement("div");
    bubble.className = "bubble assistant";
    const md = document.createElement("div");
    md.className = "md";
    md.innerHTML = renderMarkdown(text);
    bubble.append(md);
    container.append(bubble);
    this.enhanceCodeBlocks(bubble);
    if (container === this.messagesEl) this.scrollDown();
  }

  appendToolHistory(content, container = this.messagesEl) {
    const step = parseToolRow(content);
    if (step === null) return;
    const details = document.createElement("details");
    details.className = `tool history ${step.isInfo ? "info" : step.ok ? "ok" : "fail"}`;
    const summary = document.createElement("summary");
    summary.className = "tool-head";
    const name = document.createElement("span");
    name.className = "tool-name";
    name.textContent = step.tool;
    const status = document.createElement("span");
    status.className = "tool-status";
    status.textContent = step.isInfo
      ? "已注入本轮上下文"
      : `${step.ok ? "完成" : "失败"}${step.ms ? ` · ${step.ms}ms` : ""}`;
    summary.append(name, status);
    details.append(summary);
    if (step.args) {
      const pre = document.createElement("pre");
      pre.className = "tool-args";
      pre.textContent = prettyJson(step.args);
      details.append(pre);
    }
    if (step.result) {
      const pre = document.createElement("pre");
      pre.className = "tool-result";
      pre.textContent = step.result;
      details.append(pre);
    }
    container.append(details);
    if (container === this.messagesEl) this.scrollDown();
  }

  appendReasoningHistory(text, container = this.messagesEl) {
    if (!text) return;
    const details = document.createElement("details");
    details.className = "reasoning";
    const summary = document.createElement("summary");
    summary.textContent = "思考过程";
    const body = document.createElement("div");
    body.className = "reasoning-body";
    body.textContent = text;
    details.append(summary, body);
    container.append(details);
  }

  // ---- 实时流 ----

  ensureAssistant() {
    if (this.assistantEl === null) {
      const bubble = document.createElement("div");
      bubble.className = "bubble assistant";
      const md = document.createElement("div");
      md.className = "md";
      bubble.append(md);
      this.messagesEl.append(bubble);
      this.assistantEl = bubble;
      this.assistantText = "";
    }
    return this.assistantEl.querySelector(".md") || this.assistantEl.querySelector("div");
  }

  /** 流式增量渲染节流：每帧最多重渲染一次（大段输出下显著降 CPU）。 */
  queueAssistantRender(md) {
    if (this.renderQueued) return;
    this.renderQueued = true;
    // 关键：排队时捕获文本快照——done 会立刻 resetRun() 清空 assistantText，
    // 若渲染仍读实时字段，最后一段结论会被清成空气泡（"末尾没有结果"）。
    const text = this.assistantText;
    const render = () => {
      this.renderQueued = false;
      if (md) md.innerHTML = renderMarkdown(text);
      this.scrollDown();
    };
    if (typeof requestAnimationFrame === "function") requestAnimationFrame(render);
    else setTimeout(render, 16);
  }

  /** 同步落盘最后一段助手文本（done/error/关闭前调用，避免节流窗口内丢结论）。 */
  flushAssistantRender() {
    if (this.assistantEl !== null) {
      const md = this.assistantEl.querySelector(".md") || this.assistantEl.querySelector("div");
      if (md) md.innerHTML = renderMarkdown(this.assistantText);
    }
    this.renderQueued = false;
  }

  /**
   * 以落库内容重建当前会话（权威视图）：修复流式丢帧导致的"结论内容不全"，
   * 同时把实时工具卡片替换为带结果的历史卡片。
   */
  async reloadFromStore() {
    const sessionId = this.sessionId;
    if (sessionId === null) return;
    try {
      const rows = await api(`/api/sessions/${sessionId}/messages?limit=${this.pageSize}`);
      if (this.sessionId !== sessionId) return;
      this.messagesEl.replaceChildren();
      this.resetRun();
      this.oldestId = null;
      this.lastAssistantContent = "";
      for (const row of rows) this.appendHistoryRow(row);
      this.persistedRows = rows.length;
      this.updatePaging(rows);
      this.scrollDown(true);
    } catch {
      /* 重建失败则保留流式结果（下次打开会话仍是完整的） */
    }
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
    pre.textContent = prettyJson(frame.args || "");
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
    if (status) status.textContent = frame.ok ? `完成${ms}` : `失败${ms}`;
    this.lastToolEl = null;
  }

  appendInfo(label, text) {
    const el = document.createElement("div");
    el.className = "memory";
    el.textContent = `${label}: ${text}`;
    this.messagesEl.append(el);
  }

  /** 代码块加「复制」按钮（no external deps；内容取 textContent，不解析 HTML）。 */
  enhanceCodeBlocks(root) {
    const blocks = root.querySelectorAll ? root.querySelectorAll("pre") : [];
    for (const pre of blocks) {
      if (pre.querySelector?.("button")) continue;
      const wrap = document.createElement("div");
      wrap.className = "code-wrap";
      pre.parentNode?.insertBefore?.(wrap, pre);
      wrap.append(pre);
      const button = document.createElement("button");
      button.type = "button";
      button.className = "code-copy";
      button.textContent = "复制";
      button.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(pre.textContent || "");
          button.textContent = "已复制";
          setTimeout(() => (button.textContent = "复制"), 1200);
        } catch {
          button.textContent = "复制失败";
        }
      });
      wrap.append(button);
    }
  }

  resetRun() {
    this.assistantEl = null;
    this.assistantText = "";
    this.reasoningEl = null;
    this.reasoningText = "";
    this.lastToolEl = null;
    this.renderQueued = false;
  }

  setBusy(busy) {
    this.busy = busy;
    this.inputEl.disabled = busy;
    if (this.stopBtn) {
      // 旧布局：发送/停止两个按钮
      this.sendBtn.disabled = busy;
      this.stopBtn.disabled = !busy;
    } else if (this.sendBtn) {
      // 桌面风格：发送/停止合并为一个按钮（busy 时点击 = 停止）。
      // 必须显式解禁：close() 在 sessionId=null 时禁用按钮，open() 重新选中会话后
      // 若只走本分支，按钮会一直停留在 disabled（Enter 走 requestSubmit 不受影响）。
      this.sendBtn.disabled = false;
      this.sendBtn.textContent = busy ? "停止" : "发送";
      this.sendBtn.classList.toggle("is-stop", busy);
      this.sendBtn.title = busy ? "停止本轮任务" : "发送（Enter）";
    }
    if (this.sessionId === null) {
      this.inputEl.disabled = true;
      this.sendBtn.disabled = true;
      if (this.stopBtn) this.stopBtn.disabled = true;
    }
  }

  isNearBottom() {
    const el = this.messagesEl;
    const scrollHeight = el.scrollHeight ?? 0;
    const scrollTop = el.scrollTop ?? 0;
    const clientHeight = el.clientHeight ?? 0;
    return scrollHeight - scrollTop - clientHeight < 120;
  }

  scrollDown(force = false) {
    if (!force && !this.stick) return;
    this.messagesEl.scrollTop = this.messagesEl.scrollHeight;
    this.stick = true;
    if (this.toBottomEl) this.toBottomEl.hidden = true;
  }

  /** 断线时回合未结束：轮询库内消息，直到本轮结果落库（Web 在回合结束才写 assistant 消息）。 */
  async pollDroppedTurnResult() {
    if (this.recovering) return;
    this.recovering = true;
    if (this.toBottomEl) this.toBottomEl.hidden = true;
    const deadline = Date.now() + 120000;
    try {
      while (Date.now() < deadline && this.sessionId !== null) {
        await new Promise((r) => setTimeout(r, 3000));
        if (this.sessionId === null) return;
        try {
          const rows = await api(`/api/sessions/${this.sessionId}/messages?limit=1`);
          const last = rows[rows.length - 1];
          if (last?.role === "assistant" && String(last.content ?? "") !== this.lastAssistantContent) {
            await this.reloadFromStore();
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
}
