// 长任务面板：job_list / job_status / job_cancel，轮询运行中任务并展示进度/事件。
import { api } from "./api.js";

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

export class JobsPanel {
  constructor({ sheetEl, listEl, detailEl, stateEl, autoRefreshEl, refreshBtn, closeBtn, cancelBtn, resultBtn, getSessionId, onToast }) {
    this.sheetEl = sheetEl;
    this.listEl = listEl;
    this.detailEl = detailEl;
    this.stateEl = stateEl;
    this.autoRefreshEl = autoRefreshEl;
    this.refreshBtn = refreshBtn;
    this.closeBtn = closeBtn;
    this.cancelBtn = cancelBtn;
    this.resultBtn = resultBtn;
    this.getSessionId = getSessionId;
    this.onToast = onToast;
    this.items = [];
    this.selectedId = null;
    this.timer = null;
    this.loading = false;
    this.knownStates = new Map();
    this.firstLoad = true;
    this.showingResult = false;
    this.resultJobId = null;
    this.detailJobId = null;
    this.detailSignature = null;

    this.stateEl.addEventListener("change", () => this.reload());
    this.refreshBtn.addEventListener("click", () => this.reload());
    this.closeBtn.addEventListener("click", () => this.close());
    this.cancelBtn.addEventListener("click", () => this.cancelSelected());
    this.resultBtn.addEventListener("click", () => this.showResult());
  }

  isOpen() {
    return !this.sheetEl.hidden;
  }

  open() {
    this.sheetEl.hidden = false;
    this.reload();
    if (this.timer === null) this.timer = setInterval(() => this.refreshQuietly(), 2000);
  }

  close() {
    this.sheetEl.hidden = true;
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  reload() {
    this.load().catch((err) => this.onToast(`任务列表加载失败: ${err.message}`));
  }

  async refreshQuietly() {
    if (!this.isOpen()) return;
    if (this.autoRefreshEl && !this.autoRefreshEl.checked) return;
    if (typeof document !== "undefined" && document.visibilityState === "hidden") return;
    try {
      await this.load({ quiet: true });
    } catch {
      // 自动刷新失败不打扰用户
    }
  }

  async load({ quiet = false } = {}) {
    if (this.loading) return;
    const sessionId = this.getSessionId();
    if (!sessionId) {
      this.listEl.replaceChildren(el("div", "empty", "请先选择会话"));
      return;
    }
    this.loading = true;
    try {
      const state = this.stateEl.value;
      const query = new URLSearchParams({ limit: "50" });
      if (state) query.set("state", state);
      const data = await api(`/api/sessions/${sessionId}/jobs?${query}`);
      this.items = data?.jobs || [];
      this.notifyTransitions();
      this.renderList();
      if (this.items.length === 0) {
        this.selectedId = null;
        this.showingResult = false;
        this.resultJobId = null;
        this.detailJobId = null;
        this.detailSignature = null;
        this.cancelBtn.disabled = true;
        this.resultBtn.disabled = true;
        if (!quiet) this.detailEl.textContent = "当前没有任务";
        return;
      }
      if (!this.items.some((item) => item.jobId === this.selectedId)) {
        this.selectedId = this.items[0].jobId;
        this.showingResult = false;
        this.resultJobId = null;
        this.detailJobId = null;
        this.detailSignature = null;
      }

      const selectedItem = this.items.find((item) => item.jobId === this.selectedId);
      const signature = selectedItem ? jobSignature(selectedItem) : null;
      const shouldLoadDetail = !this.showingResult || this.selectedId !== this.resultJobId;
      if (shouldLoadDetail &&
          (this.selectedId !== this.detailJobId || signature !== this.detailSignature)) {
        await this.loadDetail({ quiet: true });
        this.detailJobId = this.selectedId;
        this.detailSignature = signature;
      }
    } finally {
      this.loading = false;
    }
  }

  renderList() {
    if (this.items.length === 0) {
      this.listEl.replaceChildren(el("div", "empty", "当前没有任务"));
      return;
    }
    const rows = this.items.map((item) => {
      const row = el("button", "ops-incident" + (item.jobId === this.selectedId ? " active" : ""));
      row.type = "button";
      const progress = Number(item.progress || 0);
      row.append(
        el("span", "ops-incident-title", `[${item.state}] ${item.operation || ""}`),
        el("span", "ops-incident-meta", `${item.jobId} · ${progress.toFixed(0)}% · ${formatTime(item.createdAt)}`),
        el("span", "ops-incident-preview", item.errorStatus || item.errorText || "")
      );
      row.addEventListener("click", () => {
        this.selectedId = item.jobId;
        this.showingResult = false;
        this.resultJobId = null;
        this.detailJobId = null;
        this.detailSignature = null;
        this.renderList();
        this.loadDetail()
          .then(() => {
            this.detailJobId = this.selectedId;
            this.detailSignature = jobSignature(item);
          })
          .catch((err) => this.onToast(`任务详情加载失败: ${err.message}`));
      });
      return row;
    });
    this.listEl.replaceChildren(...rows);
  }

  async loadDetail({ quiet = false } = {}) {
    if (!this.selectedId) return;
    const sessionId = this.getSessionId();
    if (!sessionId) return;
    const data = await api(`/api/sessions/${sessionId}/jobs/${encodeURIComponent(this.selectedId)}`);
    this.detailEl.textContent = formatJob(data);
    this.cancelBtn.disabled = !isRunning(data);
    this.resultBtn.disabled = !isTerminalState(data?.state);
    if (!quiet) this.detailEl.scrollTop = 0;
  }

  async showResult() {
    if (!this.selectedId) return;
    const sessionId = this.getSessionId();
    if (!sessionId) return;
    const jobId = this.selectedId;
    this.showingResult = true;
    this.resultJobId = jobId;
    try {
      const data = await api(`/api/sessions/${sessionId}/jobs/${encodeURIComponent(jobId)}/result`);
      this.detailEl.textContent = formatResult(data);
      this.detailEl.scrollTop = 0;
    } catch (err) {
      this.showingResult = false;
      this.resultJobId = null;
      this.onToast(`结果加载失败: ${err.message}`);
    }
  }

  notifyTransitions() {
    for (const item of this.items) {
      const previous = this.knownStates.get(item.jobId);
      if (!this.firstLoad && previous && !isTerminalState(previous) && isTerminalState(item.state)) {
        this.onToast(`长任务 ${item.operation || item.jobId} ${item.state}`);
      }
      this.knownStates.set(item.jobId, item.state);
    }
    this.firstLoad = false;
  }

  async cancelSelected() {
    if (!this.selectedId) return;
    const sessionId = this.getSessionId();
    if (!sessionId) return;
    this.showingResult = false;
    this.resultJobId = null;
    this.cancelBtn.disabled = true;
    try {
      await api(`/api/sessions/${sessionId}/jobs/${encodeURIComponent(this.selectedId)}/cancel`, { method: "POST" });
      this.onToast("已请求取消任务");
      await this.load();
    } catch (err) {
      this.onToast(`取消失败: ${err.message}`);
      this.cancelBtn.disabled = false;
    }
  }
}

function formatTime(value) {
  if (!value) return "-";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function jobSignature(item) {
  return `${item?.state || ""}|${Number(item?.progress || 0).toFixed(0)}|${item?.errorStatus || ""}`;
}

function isRunning(data) {
  return ["pending", "running"].includes(String(data?.state || ""));
}

function isTerminalState(state) {
  return ["succeeded", "failed", "cancelled", "orphaned"].includes(String(state || ""));
}

function formatResult(data) {
  if (!data) return "(空结果)";
  const lines = [
    `任务: ${data.jobId || "-"}`,
    `Operation: ${data.operation || "-"}`,
    `状态: ${data.state || "-"}`,
  ];
  if (data.error) lines.push(`错误: ${data.error}`);
  lines.push("", "结果 JSON:");
  lines.push(data.result !== undefined && data.result !== null
    ? JSON.stringify(data.result, null, 2)
    : "(无结果 payload)");
  return lines.join("\n");
}

function formatJob(data) {
  if (!data) return "(空结果)";
  const lines = [
    `任务: ${data.jobId || "-"}`,
    `Operation: ${data.operation || "-"}`,
    `状态: ${data.state || "-"}`,
    `进度: ${data.progressDone || 0}/${data.progressTotal || 0} (${Number(data.progress || 0).toFixed(0)}%)`,
    `取消请求: ${data.cancelRequested ? "是" : "否"}   部分结果: ${data.partial ? "是" : "否"}`,
    `创建: ${formatTime(data.createdAt)}   完成: ${formatTime(data.finishedAt)}`,
  ];
  if (data.errorStatus) lines.push(`错误状态: ${data.errorStatus}`);
  if (data.errorText) lines.push(`错误: ${data.errorText}`);
  if (Array.isArray(data.recentEvents) && data.recentEvents.length > 0) {
    lines.push("", "最近事件:");
    for (const evt of data.recentEvents) {
      lines.push(`- #${evt.seq} [${evt.kind}] ${evt.createdAt || ""}`);
      const detail = evt.data ? JSON.stringify(evt.data) : (evt.message || "");
      if (detail) lines.push(`  ${detail}`);
    }
  }
  return lines.join("\n");
}
