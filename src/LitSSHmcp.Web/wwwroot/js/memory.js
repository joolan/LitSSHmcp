// 管理员「团队记忆库」：列表 / 历史版本 / 回滚 / 归档。
// 仅 admin 角色显示入口；所有写操作走 /api/admin/memory（服务端再次校验角色 + CSRF）。
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

export class MemoryAdminPanel {
  constructor({ sheetEl, listEl, versionsEl, kindEl, archiveBtn, pruneBtn, refreshBtn, closeBtn, currentEl, onToast }) {
    this.sheetEl = sheetEl;
    this.listEl = listEl;
    this.versionsEl = versionsEl;
    this.kindEl = kindEl;
    this.archiveBtn = archiveBtn;
    this.pruneBtn = pruneBtn;
    this.refreshBtn = refreshBtn;
    this.closeBtn = closeBtn;
    this.currentEl = currentEl;
    this.onToast = onToast;

    this.items = [];
    this.selected = null;

    this.kindEl.addEventListener("change", () => this.reload());
    this.refreshBtn.addEventListener("click", () => this.reload());
    this.closeBtn.addEventListener("click", () => this.close());
    this.archiveBtn.addEventListener("click", () => this.archive());
    this.pruneBtn?.addEventListener("click", () => this.pruneHistory());
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
    this.load().catch((err) => this.onToast(`记忆库加载失败: ${err.message}`));
  }

  async load() {
    const kind = this.kindEl.value;
    const data = await api(`/api/admin/memory${kind ? `?kind=${encodeURIComponent(kind)}` : ""}`);
    this.items = data.items || [];
    this.renderList();
    if (this.selected) {
      const current = this.items.find((item) => item.id === this.selected.id);
      if (current) {
        await this.select(current);
      } else {
        this.selected = null;
        this.archiveBtn.disabled = true;
        this.currentEl.textContent = "";
        this.renderVersions([]);
      }
    }
  }

  renderList() {
    if (this.items.length === 0) {
      this.listEl.replaceChildren(el("div", "empty", "暂无记忆（AI 成功任务后会自动积累，或让 AI 用 ops_memory_write(mode=save) 写入）"));
      return;
    }
    const rows = this.items.map((item) => {
      const row = el("button", "memory-row" + (this.selected?.id === item.id ? " active" : ""));
      row.type = "button";
      row.append(
        el("span", "m-title", `[${item.kind}:${item.key || "会话片段"}] ×${item.freq} · ${item.status}`),
        el("span", "m-meta", `#${item.id} · 更新 ${item.updatedAt || "-"}`),
        el("span", "m-preview", truncate(item.content, 140))
      );
      row.addEventListener("click", () => {
        this.select(item).catch((err) => this.onToast(`历史版本加载失败: ${err.message}`));
      });
      return row;
    });
    this.listEl.replaceChildren(...rows);
  }

  async select(item) {
    this.selected = item;
    this.archiveBtn.disabled = false;
    this.currentEl.textContent = `[${item.kind}:${item.key || "会话片段"}]`;
    this.renderList();
    const data = await api(`/api/admin/memory/${item.id}/versions`);
    this.renderVersions(data.items || []);
  }

  renderVersions(items) {
    if (!items || items.length === 0) {
      this.versionsEl.replaceChildren(el("div", "empty", "暂无历史版本（首次更新/删除/归档后才会产生快照）"));
      return;
    }
    const rows = items.map((version) => {
      const row = el("div", "memory-version");
      const body = el("div", "mv-body");
      body.append(
        el("div", "mv-title", `v${version.version} · ${version.status} · 使用 ${version.freq} 次`),
        el("div", "mv-meta", `归档 ${version.archivedAt || "-"}`),
        el("div", "mv-preview", truncate(version.content, 200))
      );
      const btn = el("button", "ghost", `回滚到 v${version.version}`);
      btn.type = "button";
      btn.addEventListener("click", () => this.rollback(version));
      row.append(body, btn);
      return row;
    });
    this.versionsEl.replaceChildren(...rows);
  }

  async rollback(version) {
    if (!this.selected) return;
    const name = `[${this.selected.kind}:${this.selected.key || "会话片段"}]`;
    if (!window.confirm(`回滚 ${name} 到 v${version.version}？\n\n当前状态会先归档，回滚本身也可再次撤销。`)) return;
    try {
      await api(`/api/admin/memory/${this.selected.id}/rollback`, {
        method: "POST",
        body: { version: version.version },
      });
      this.onToast(`已回滚 ${name} 到 v${version.version}`);
      await this.load();
    } catch (err) {
      this.onToast(`回滚失败: ${err.message}`);
    }
  }

  async pruneHistory() {
    if (!window.confirm("清理历史快照？\n\n每个记忆保留最近 40 个版本；同时清理超过 30 天的孤儿历史。")) return;
    try {
      const data = await api("/api/admin/memory/history/prune", {
        method: "POST",
        body: { keep: 20 },
      });
      this.onToast(`已清理 ${data.removed ?? 0} 个旧快照`);
      await this.load();
    } catch (err) {
      this.onToast(`清理历史失败: ${err.message}`);
    }
  }

  async archive() {
    if (!this.selected) return;
    const name = `[${this.selected.kind}:${this.selected.key || "会话片段"}]`;
    if (!window.confirm(`归档 ${name}？\n\n归档后不再被召回，历史版本保留，可随时回滚恢复。`)) return;
    try {
      await api(`/api/admin/memory/${this.selected.id}/archive`, { method: "POST" });
      this.onToast(`已归档 ${name}`);
      this.selected = null;
      this.archiveBtn.disabled = true;
      this.currentEl.textContent = "";
      this.renderVersions([]);
      await this.load();
    } catch (err) {
      this.onToast(`归档失败: ${err.message}`);
    }
  }
}
