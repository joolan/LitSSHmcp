// 会话目录浏览/下载面板（M3-6）：列表/面包屑/下载链接。条目名一律 textContent（文件系统名不可信）。
import { api, downloadUrl } from "./api.js";

export class FilesPanel {
  constructor({ paneEl, pathEl, listEl, upBtn, closeBtn, onToast, onClosed, onOpened }) {
    this.paneEl = paneEl;
    this.pathEl = pathEl;
    this.listEl = listEl;
    this.upBtn = upBtn;
    this.closeBtn = closeBtn;
    this.onToast = onToast;
    this.onClosed = onClosed;
    this.onOpened = onOpened;
    this.sessionId = null;
    this.path = "";
    this.closeBtn.addEventListener("click", () => this.close());
    this.upBtn.addEventListener("click", () => {
      if (this.path === "") return;
      const parts = this.path.split("/").filter(Boolean);
      parts.pop();
      this.path = parts.join("/");
      this.refresh().catch((err) => this.onToast(err.message));
    });
  }

  async open(sessionId) {
    this.sessionId = sessionId;
    this.path = "";
    this.paneEl.hidden = false;
    if (this.onOpened) this.onOpened();
    await this.refresh();
  }

  close() {
    this.paneEl.hidden = true;
    this.sessionId = null;
    if (this.onClosed) this.onClosed();
  }

  async refresh() {
    if (this.sessionId === null) return;
    const query = this.path ? `?path=${encodeURIComponent(this.path)}` : "";
    let items;
    try {
      const data = await api(`/api/sessions/${this.sessionId}/files${query}`);
      items = data.items || [];
    } catch (err) {
      this.onToast(`目录读取失败: ${err.message}`);
      return;
    }
    this.renderBreadcrumb();
    this.upBtn.disabled = this.path === "";

    const nodes = [];
    for (const entry of items) {
      if (entry.isDir) {
        const row = document.createElement("button");
        row.className = "file-row dir";
        const name = document.createElement("span");
        name.className = "fname";
        name.textContent = entry.name;
        const tag = document.createElement("span");
        tag.className = "ftag";
        tag.textContent = "目录";
        row.append(name, tag);
        row.addEventListener("click", () => {
          this.path = entry.path;
          this.refresh().catch((err) => this.onToast(err.message));
        });
        nodes.push(row);
      } else {
        const a = document.createElement("a");
        a.className = "file-row file";
        a.href = downloadUrl(this.sessionId, entry.path);
        a.setAttribute("download", "");
        const name = document.createElement("span");
        name.className = "fname";
        name.textContent = entry.name;
        const meta = document.createElement("span");
        meta.className = "ftag";
        meta.textContent = formatSize(entry.size);
        a.append(name, meta);
        nodes.push(a);
      }
    }
    if (nodes.length === 0) {
      const empty = document.createElement("div");
      empty.className = "empty";
      empty.textContent = "(空目录)";
      nodes.push(empty);
    }
    this.listEl.replaceChildren(...nodes);
  }

  renderBreadcrumb() {
    const parts = this.path.split("/").filter(Boolean);
    const nodes = [];
    const root = document.createElement("button");
    root.className = "crumb";
    root.textContent = "会话目录";
    root.addEventListener("click", () => {
      this.path = "";
      this.refresh().catch((err) => this.onToast(err.message));
    });
    nodes.push(root);
    for (let i = 0; i < parts.length; i++) {
      const sep = document.createElement("span");
      sep.className = "crumb-sep";
      sep.textContent = " / ";
      nodes.push(sep);
      const crumb = document.createElement("button");
      crumb.className = "crumb";
      crumb.textContent = parts[i];
      const target = parts.slice(0, i + 1).join("/");
      crumb.addEventListener("click", () => {
        this.path = target;
        this.refresh().catch((err) => this.onToast(err.message));
      });
      nodes.push(crumb);
    }
    this.pathEl.replaceChildren(...nodes);
  }
}

function formatSize(size) {
  if (typeof size !== "number") return "";
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  if (size < 1024 * 1024 * 1024) return `${(size / 1024 / 1024).toFixed(1)} MB`;
  return `${(size / 1024 / 1024 / 1024).toFixed(1)} GB`;
}
