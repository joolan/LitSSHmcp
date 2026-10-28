// 运维视图：事件浏览 + 时间线 + 资产画像（登录用户；服务端按团队/私有作用域过滤）。
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

export class OpsInsightsPanel {
  constructor({
    sheetEl,
    incidentsEl,
    statusEl,
    refreshBtn,
    closeBtn,
    timelineEl,
    assetIdEl,
    assetLoadBtn,
    profileEl,
    onToast,
  }) {
    this.sheetEl = sheetEl;
    this.incidentsEl = incidentsEl;
    this.statusEl = statusEl;
    this.refreshBtn = refreshBtn;
    this.closeBtn = closeBtn;
    this.timelineEl = timelineEl;
    this.assetIdEl = assetIdEl;
    this.assetLoadBtn = assetLoadBtn;
    this.profileEl = profileEl;
    this.onToast = onToast;
    this.items = [];
    this.selectedId = null;

    this.statusEl.addEventListener("change", () => this.reload());
    this.refreshBtn.addEventListener("click", () => this.reload());
    this.closeBtn.addEventListener("click", () => this.close());
    this.assetLoadBtn.addEventListener("click", () => this.loadProfile());
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
    this.load().catch((err) => this.onToast(`事件加载失败: ${err.message}`));
  }

  async load() {
    const status = this.statusEl.value;
    const data = await api(`/api/ops/incidents${status ? `?status=${encodeURIComponent(status)}` : ""}`);
    this.items = data.items || [];
    this.renderList();
    if (this.items.length === 0) {
      this.timelineEl.textContent = "暂无事件";
      this.selectedId = null;
      return;
    }
    if (!this.items.some((item) => item.id === this.selectedId))
      this.selectedId = this.items[0].id;
    const selected = this.items.find((item) => item.id === this.selectedId);
    if (selected) await this.selectIncident(selected, { quiet: true });
  }

  renderList() {
    if (this.items.length === 0) {
      this.incidentsEl.replaceChildren(el("div", "empty", "暂无事件。完成一次诊断/变更后，事件会自动记录。"));
      return;
    }
    const rows = this.items.map((item) => {
      const row = el("button", "ops-incident" + (item.id === this.selectedId ? " active" : ""));
      row.type = "button";
      row.append(
        el("span", "ops-incident-title", `[${item.status}] ${item.title}`),
        el("span", "ops-incident-meta", `#${item.id} · ${item.assetIds || "-"} · ${item.updatedAt || "-"}`),
        el("span", "ops-incident-preview", truncate(item.rootCause || item.symptom || "", 160))
      );
      row.addEventListener("click", () => {
        this.selectIncident(item).catch((err) => this.onToast(`时间线加载失败: ${err.message}`));
      });
      return row;
    });
    this.incidentsEl.replaceChildren(...rows);
  }

  async selectIncident(item, { quiet = false } = {}) {
    this.selectedId = item.id;
    this.renderList();
    const data = await api(`/api/ops/timeline?incidentId=${item.id}&limit=200`);
    this.timelineEl.textContent = data.text || "(无时间线)";
    if (!quiet) this.timelineEl.scrollTop = 0;
  }

  async loadProfile() {
    const assetId = (this.assetIdEl.value || "").trim();
    if (!assetId) {
      this.onToast("请输入资产 ID");
      return;
    }
    try {
      const data = await api(`/api/ops/asset-profile?assetId=${encodeURIComponent(assetId)}&limit=10`);
      this.profileEl.textContent = data.text || "(无资产画像)";
    } catch (err) {
      this.onToast(`资产画像加载失败: ${err.message}`);
    }
  }
}
