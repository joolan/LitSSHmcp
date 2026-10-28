// 管理员「记忆候选审核」：后台 consolidation 生成的事件候选（批准写入事件 / 拒绝）。
// 仅 admin 显示入口；所有写操作走 /api/admin/consolidation/candidates（服务端再次校验角色 + CSRF）。
import { api } from "./api.js";

const truncate = (text, max) => {
  const value = text || "";
  return value.length <= max ? value : `${value.slice(0, max)}…`;
};

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function titleFromPayload(payload) {
  try {
    const data = JSON.parse(payload || "{}");
    if (data.title) return data.title;
    if (data.assetId && data.key) return `${data.assetId} · ${data.key}`;
    return "(无标题)";
  } catch {
    return "(payload 解析失败)";
  }
}

function prettyPayload(payload) {
  try {
    return JSON.stringify(JSON.parse(payload || "{}"), null, 2);
  } catch {
    return payload || "";
  }
}

export class ConsolidationAdminPanel {
  constructor({ sheetEl, listEl, runsEl, alertEl, statusEl, refreshBtn, closeBtn, onToast }) {
    this.sheetEl = sheetEl;
    this.listEl = listEl;
    this.runsEl = runsEl;
    this.alertEl = alertEl;
    this.statusEl = statusEl;
    this.refreshBtn = refreshBtn;
    this.closeBtn = closeBtn;
    this.onToast = onToast;
    this.items = [];

    this.statusEl.addEventListener("change", () => this.reload());
    this.refreshBtn.addEventListener("click", () => this.reload());
    this.closeBtn.addEventListener("click", () => this.close());
  }

  isOpen() {
    return !this.sheetEl.hidden;
  }

  open() {
    this.sheetEl.hidden = false;
    this.reload();
  }

  close() {
    this.sheetEl.hidden = true;
  }

  reload() {
    this.load().catch((err) => this.onToast(`候选加载失败: ${err.message}`));
  }

  async load() {
    const status = this.statusEl.value;
    const data = await api(`/api/admin/consolidation/candidates${status ? `?status=${encodeURIComponent(status)}` : ""}`);
    this.items = data.items || [];
    this.renderList();
    if (this.runsEl) await this.loadRuns();
  }

  async loadRuns() {
    try {
      const data = await api("/api/admin/consolidation/runs?limit=20");
      const items = data.items || [];
      const failed = items.filter((run) => run.status === "failed" || run.error);
      if (this.alertEl) {
        this.alertEl.hidden = failed.length === 0;
        this.alertEl.textContent = failed.length === 0
          ? ""
          : `最近 ${items.length} 次 consolidation 有 ${failed.length} 次失败；最近：${failed[0].error || failed[0].status}（${failed[0].startedAt || "-"}）`;
      }
      if (items.length === 0) {
        this.runsEl.replaceChildren(el("div", "empty", "暂无运行记录。"));
        return;
      }
      this.runsEl.replaceChildren(...items.map((run) => {
        const row = el("div", "consolidation-row");
        row.append(
          el("div", "ops-incident-title",
            `${run.status} · raw ${run.rawCount} · 候选 ${run.candidateCount} · 自动写入 ${run.appliedCount}`),
          el("div", "ops-incident-meta",
            `${run.startedAt || ""}${run.error ? ` · ${run.error}` : ""}`)
        );
        return row;
      }));
    } catch (err) {
      this.runsEl.textContent = `运行历史加载失败：${err.message}`;
    }
  }

  renderList() {
    if (this.items.length === 0) {
      this.listEl.replaceChildren(el("div", "empty", "暂无候选。开启 agent.consolidation 并完成若干任务后，后台会生成事件候选。"));
      return;
    }

    const rows = this.items.map((item) => {
      const row = el("div", "consolidation-row");
      const head = el("div", "c-head");
      head.append(
        el("span", "c-title", `[${item.status}] ${titleFromPayload(item.payload)}`),
        el("span", "c-meta", `#${item.id} · ${item.kind} · 置信度 ${Number(item.confidence || 0).toFixed(2)}`)
      );
      row.append(head);
      row.append(el("div", "c-meta", `资产: ${item.assetIds || "-"} · 来源: ${item.sourceRefs || "-"}`));
      row.append(el("div", "c-meta", `证据: ${truncate(item.evidence, 240) || "-"}`));
      row.append(el("pre", "c-payload", truncate(prettyPayload(item.payload), 900)));

      if (item.status === "pending" || item.status === "conflict") {
        const actions = el("div", "c-actions");
        const approve = el("button", "ghost", "批准应用");
        approve.type = "button";
        approve.addEventListener("click", () => this.approve(item));
        const reject = el("button", "ghost", "拒绝");
        reject.type = "button";
        reject.addEventListener("click", () => this.reject(item));
        actions.append(approve, reject);
        row.append(actions);
      } else if (item.appliedRef) {
        row.append(el("div", "c-meta", `已应用: ${item.appliedRef}`));
      } else if (item.rejectReason) {
        row.append(el("div", "c-meta", `拒绝原因: ${item.rejectReason}`));
      }
      return row;
    });
    this.listEl.replaceChildren(...rows);
  }

  async approve(item) {
    if (!window.confirm(`批准候选 #${item.id} 并写入事件？\n\n${titleFromPayload(item.payload)}`)) return;
    try {
      const data = await api(`/api/admin/consolidation/candidates/${item.id}/approve`, { method: "POST" });
      this.onToast(`已批准，写入事件 #${data.incidentId ?? "-"}`);
      await this.load();
    } catch (err) {
      this.onToast(`批准失败: ${err.message}`);
    }
  }

  async reject(item) {
    const reason = window.prompt(`拒绝候选 #${item.id} 的原因（可留空）`, "重复/无证据");
    if (reason === null) return;
    try {
      await api(`/api/admin/consolidation/candidates/${item.id}/reject`, {
        method: "POST",
        body: { reason },
      });
      this.onToast(`已拒绝候选 #${item.id}`);
      await this.load();
    } catch (err) {
      this.onToast(`拒绝失败: ${err.message}`);
    }
  }
}
