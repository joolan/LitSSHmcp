// 应用入口：登录/会话列表/模型选择/抽屉与聊天接线（聊天、审批、目录由各模块接管）。
import { api } from "./api.js";
import { ChatController } from "./chat.js";
import { ApprovalsPanel } from "./approvals.js";
import { FilesPanel } from "./files.js";
import { MemoryAdminPanel } from "./memory.js";

const $ = (id) => document.getElementById(id);

const els = {
  loginView: $("login-view"),
  loginForm: $("login-form"),
  loginUser: $("login-user"),
  loginPass: $("login-pass"),
  loginBtn: $("login-btn"),
  loginError: $("login-error"),
  appView: $("app-view"),
  whoami: $("whoami"),
  logoutBtn: $("logout-btn"),
  menuBtn: $("menu-btn"),
  paneClose: $("pane-close"),
  backdrop: $("backdrop"),
  newSessionBtn: $("new-session-btn"),
  sessionList: $("session-list"),
  sessionTitle: $("session-title"),
  turnStatus: $("turn-status"),
  connDot: $("conn-dot"),
  filesBtn: $("files-btn"),
  modelBtn: $("model-btn"),
  modelSheet: $("model-sheet"),
  modelList: $("model-list"),
  modelClose: $("model-close"),
  approvalStrip: $("approval-strip"),
  approvalOverlay: $("approval-overlay"),
  approvalBadge: $("approval-badge"),
  approvalCount: $("approval-count"),
  approvalCollapse: $("approval-collapse"),
  memoryAdminBtn: $("memory-admin-btn"),
  memorySheet: $("memory-admin"),
  memoryList: $("memory-list"),
  memoryVersions: $("memory-versions"),
  memoryKind: $("memory-kind"),
  memoryArchive: $("memory-archive"),
  memoryPrune: $("memory-prune"),
  memoryRefresh: $("memory-refresh"),
  memoryClose: $("memory-close"),
  memoryCurrent: $("memory-current"),
  messages: $("messages"),
  toBottom: $("to-bottom"),
  loadOlder: $("load-older"),
  composer: $("composer"),
  input: $("input"),
  sendBtn: $("send-btn"),
  attachBtn: $("attach-btn"),
  fileInput: $("file-input"),
  chips: $("attachment-chips"),
  filesPane: $("files-pane"),
  filesPath: $("files-path"),
  filesList: $("files-list"),
  filesUp: $("files-up"),
  filesClose: $("files-close"),
  imageViewer: $("image-viewer"),
  imageViewerImg: $("image-viewer-img"),
  imageViewerClose: $("image-viewer-close"),
  toast: $("toast"),
};

let me = null;
let sessions = [];
let activeId = null;
let toastTimer = null;
let providers = { activeId: null, items: [] };
const isCoarsePointer = () => window.matchMedia?.("(pointer: coarse)")?.matches ?? false;

const chat = new ChatController({
  messagesEl: els.messages,
  inputEl: els.input,
  sendBtn: els.sendBtn,
  stopBtn: null, // 发送/停止合并为一个按钮（busy 时点击 = 停止）
  attachBtn: els.attachBtn,
  fileInput: els.fileInput,
  chipsEl: els.chips,
  toBottomEl: els.toBottom,
  loadOlderBtn: els.loadOlder,
  onToast,
  onStatus: (text) => {
    els.turnStatus.textContent = text;
  },
  onConnState: (state) => {
    els.connDot.className = "dot" + (state === "ok" ? " ok" : state === "down" ? " down" : "");
    els.connDot.title = state === "ok" ? "已连接" : state === "down" ? "连接断开" : "连接状态";
  },
});

const approvals = new ApprovalsPanel({
  stripEl: els.approvalStrip,
  overlayEl: els.approvalOverlay,
  badgeEl: els.approvalBadge,
  countEl: els.approvalCount,
  onToast,
});

els.approvalCollapse.addEventListener("click", () => approvals.setCollapsed(true));
els.approvalBadge.addEventListener("click", () => approvals.setCollapsed(false));

const files = new FilesPanel({
  paneEl: els.filesPane,
  pathEl: els.filesPath,
  listEl: els.filesList,
  upBtn: els.filesUp,
  closeBtn: els.filesClose,
  onToast,
  onOpened: () => els.filesBtn.classList.add("active"),
  onClosed: () => els.filesBtn.classList.remove("active"),
});

const memoryAdmin = new MemoryAdminPanel({
  sheetEl: els.memorySheet,
  listEl: els.memoryList,
  versionsEl: els.memoryVersions,
  kindEl: els.memoryKind,
  archiveBtn: els.memoryArchive,
  pruneBtn: els.memoryPrune,
  refreshBtn: els.memoryRefresh,
  closeBtn: els.memoryClose,
  currentEl: els.memoryCurrent,
  onToast,
});

els.memoryAdminBtn.addEventListener("click", () => memoryAdmin.open());

function onToast(message) {
  els.toast.textContent = message;
  els.toast.hidden = false;
  if (toastTimer !== null) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    els.toast.hidden = true;
    toastTimer = null;
  }, 4500);
}

// 输入框禁用时（未选会话）给出可操作的提示，避免"不能输入也没原因"。
function updatePlaceholder() {
  els.input.placeholder = activeId === null
    ? "请先点击左上方「＋ 新会话」创建或选择会话，再输入任务…"
    : "输入任务…（Enter 发送，Shift+Enter 换行）";
}

// ---- 抽屉（手机） ----
function openDrawer() {
  document.body.classList.add("drawer-open");
  els.backdrop.hidden = false;
}
function closeDrawer() {
  document.body.classList.remove("drawer-open");
  els.backdrop.hidden = true;
}
els.menuBtn.addEventListener("click", openDrawer);
els.paneClose.addEventListener("click", closeDrawer);
els.backdrop.addEventListener("click", closeDrawer);

// ---- 视图切换 ----
function showLogin() {
  els.appView.hidden = true;
  els.loginView.hidden = false;
  els.loginUser.focus();
  closeDrawer();
  closeModelSheet();
  updatePlaceholder();
}

function showApp(user) {
  me = user;
  els.loginView.hidden = true;
  els.appView.hidden = false;
  els.whoami.textContent = `${user.username}（${user.role}）`;
  els.memoryAdminBtn.hidden = user.role !== "admin";
  if (user.role !== "admin") memoryAdmin.close();
  updatePlaceholder();
  approvals.start();
  loadProviders().catch(() => {});
  loadSessions().catch((err) => onToast(err.message));
}

// ---- 登录/登出 ----
els.loginForm.addEventListener("submit", async (ev) => {
  ev.preventDefault();
  els.loginBtn.disabled = true;
  els.loginError.hidden = true;
  try {
    const data = await api("/api/login", {
      method: "POST",
      body: { username: els.loginUser.value, password: els.loginPass.value },
    });
    els.loginPass.value = "";
    showApp(data.user);
  } catch (err) {
    els.loginError.textContent = err.message || "登录失败";
    els.loginError.hidden = false;
  } finally {
    els.loginBtn.disabled = false;
  }
});

els.logoutBtn.addEventListener("click", async () => {
  try {
    await api("/api/logout", { method: "POST" });
  } catch {
    /* 忽略登出失败（本地状态照旧清理） */
  }
  approvals.stop();
  chat.close();
  files.close();
  memoryAdmin.close();
  me = null;
  sessions = [];
  activeId = null;
  els.sessionList.replaceChildren();
  els.messages.replaceChildren();
  showLogin();
});

// ---- 模型 ----
function providerLabel() {
  const session = sessions.find((s) => s.id === activeId);
  const id = session?.providerId || providers.activeId;
  const item = providers.items.find((p) => p.id === id);
  return item ? item.display || item.model || item.name : "模型…";
}

function updateModelButton() {
  els.modelBtn.textContent = `${providerLabel()} ▾`;
  els.modelBtn.title = "切换本会话模型";
}

async function loadProviders() {
  const data = await api("/api/agent/providers");
  providers = { activeId: data.activeId || null, items: data.providers || [] };
  updateModelButton();
}

function openModelSheet() {
  if (activeId === null) {
    onToast("请先选择或新建会话，再切换模型");
    return;
  }
  els.modelSheet.hidden = false;
  positionModelSheet();
  renderModelSheet();
}

/** 电脑端：弹层贴着「模型」按钮上方出现（手机端沿用底部弹层样式）。 */
function positionModelSheet() {
  const sheet = els.modelSheet;
  if (window.innerWidth <= 820) {
    sheet.style.left = "";
    sheet.style.right = "";
    sheet.style.top = "";
    sheet.style.bottom = "";
    sheet.style.width = "";
    sheet.style.maxHeight = "";
    return;
  }
  const rect = els.modelBtn.getBoundingClientRect();
  const width = Math.min(380, window.innerWidth - 24);
  const left = Math.max(12, Math.min(rect.left, window.innerWidth - width - 12));
  sheet.style.width = `${width}px`;
  sheet.style.left = `${left}px`;
  sheet.style.right = "auto";
  sheet.style.top = "auto";
  sheet.style.bottom = `${Math.max(12, window.innerHeight - rect.top + 8)}px`;
  sheet.style.maxHeight = `${Math.max(160, Math.min(window.innerHeight * 0.6, rect.top - 16))}px`;
}

function closeModelSheet() {
  els.modelSheet.hidden = true;
}

function renderModelSheet() {
  const session = sessions.find((s) => s.id === activeId);
  const currentId = session?.providerId || providers.activeId;
  const nodes = providers.items.map((item) => {
    const button = document.createElement("button");
    button.className = "model-item" + (item.id === currentId ? " active" : "");
    const name = document.createElement("span");
    name.textContent = item.display || item.name || item.model || item.id;
    const meta = document.createElement("small");
    meta.textContent = item.model ? `模型：${item.model}` : "";
    button.append(name, meta);
    button.addEventListener("click", () => chooseModel(item.id).catch((err) => onToast(err.message)));
    return button;
  });
  const empty = document.createElement("div");
  empty.className = "empty";
  empty.textContent = providers.items.length === 0 ? "未配置启用的模型（在桌面 AI 助手设置添加）" : "";
  if (providers.items.length === 0) nodes.push(empty);
  els.modelList.replaceChildren(...nodes);
}

async function chooseModel(providerId) {
  const session = sessions.find((s) => s.id === activeId);
  if (!session) return;
  await api(`/api/sessions/${session.id}/provider`, {
    method: "PUT",
    body: { providerId },
  });
  session.providerId = providerId;
  updateModelButton();
  closeModelSheet();
  renderSessions();
  onToast("模型已切换（下一轮消息生效）");
}

els.modelBtn.addEventListener("click", openModelSheet);
els.modelClose.addEventListener("click", closeModelSheet);
window.addEventListener("resize", () => {
  if (!els.modelSheet.hidden) positionModelSheet();
});
document.addEventListener("click", (ev) => {
  if (!els.modelSheet.hidden) {
    if (!els.modelSheet.contains(ev.target) && !els.modelBtn.contains(ev.target))
      closeModelSheet();
  }
  if (memoryAdmin.isOpen() && !els.memorySheet.contains(ev.target) && !els.memoryAdminBtn.contains(ev.target))
    memoryAdmin.close();
});

// ---- 会话列表 ----
function relativeTime(iso) {
  if (!iso) return "";
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "";
  const diff = Math.round((then - Date.now()) / 1000);
  const abs = Math.abs(diff);
  const units = [
    [60, "秒"],
    [3600, "分钟"],
    [86400, "小时"],
    [2592000, "天"],
    [31536000, "个月"],
  ];
  if (abs < 60) return "刚刚";
  let value = diff / 60;
  let label = "分钟";
  for (let i = 1; i < units.length; i++) {
    if (abs < units[i][0]) {
      value = diff / units[i - 1][0];
      label = units[i - 1][1];
      break;
    }
    value = diff / units[i][0];
    label = units[i][1];
  }
  const rounded = Math.round(value);
  return rounded < 0 ? `${Math.abs(rounded)}${label}前` : `${rounded}${label}后`;
}

async function loadSessions() {
  sessions = await api("/api/sessions");
  renderSessions();
  updateModelButton();
}

function renderSessions() {
  updatePlaceholder();
  const nodes = [];
  for (const s of sessions) {
    const li = document.createElement("li");
    if (s.id === activeId) li.classList.add("active");
    const open = document.createElement("button");
    open.className = "session";
    open.addEventListener("click", () => selectSession(s.id).catch((err) => onToast(err.message)));
    const title = document.createElement("span");
    title.className = "session-title";
    title.textContent = s.title || `会话 ${s.id}`;
    const time = document.createElement("span");
    time.className = "session-time";
    time.textContent = relativeTime(s.updatedAt);
    open.append(title, time);
    const del = document.createElement("button");
    del.className = "session-del";
    del.textContent = "×";
    del.title = "删除会话";
    del.addEventListener("click", async (ev) => {
      ev.stopPropagation();
      if (!window.confirm(`删除会话「${title.textContent}」？其会话目录将一并清理`)) return;
      try {
        await api(`/api/sessions/${s.id}`, { method: "DELETE" });
        if (activeId === s.id) clearActive();
        await loadSessions();
      } catch (err) {
        onToast(`删除失败: ${err.message}`);
      }
    });
    li.append(open, del);
    nodes.push(li);
  }
  const empty = document.createElement("li");
  empty.className = "empty";
  empty.textContent = sessions.length === 0 ? "(暂无会话，点上方新建)" : "";
  if (sessions.length === 0) nodes.push(empty);
  els.sessionList.replaceChildren(...nodes);
}

els.newSessionBtn.addEventListener("click", async () => {
  try {
    const created = await api("/api/sessions", {
      method: "POST",
      body: { title: `任务 ${new Date().toLocaleString()}` },
    });
    await loadSessions();
    await selectSession(created.id);
  } catch (err) {
    onToast(`新建失败: ${err.message}`);
  }
});

async function selectSession(id) {
  activeId = id;
  const s = sessions.find((x) => x.id === id);
  els.sessionTitle.textContent = s ? s.title : `会话 ${id}`;
  els.filesBtn.disabled = false;
  if (!els.filesPane.hidden) {
    files.close();
    els.filesBtn.classList.remove("active");
  }
  renderSessions();
  updateModelButton();
  closeDrawer();
  closeModelSheet();
  await chat.open(id);
}

function clearActive() {
  activeId = null;
  chat.close();
  files.close();
  els.filesBtn.classList.remove("active");
  els.filesBtn.disabled = true;
  els.sessionTitle.textContent = "未选择会话";
  els.messages.replaceChildren();
  renderSessions();
  updateModelButton();
}

// ---- 聊天输入 ----
els.composer.addEventListener("submit", (ev) => {
  ev.preventDefault();
  // 发送/停止合并：运行中点击（或 Enter）即停止本轮
  if (chat.busy) {
    chat.stop();
    return;
  }
  const text = els.input.value.trim();
  if ((!text && chat.files.length === 0) || chat.uploading) return;
  els.input.value = "";
  autoGrow();
  chat.send(text).then((ok) => {
    if (ok === false) {
      // 发送失败（如连接未就绪/图片上传失败）：恢复输入内容，避免用户丢失草稿
      if (!els.input.value) els.input.value = text;
      autoGrow();
      return;
    }
    // 首条消息后服务端会把默认标题改为任务摘要：稍后刷新列表
    setTimeout(() => loadSessions().catch(() => {}), 1200);
  });
});

els.input.addEventListener("keydown", (ev) => {
  if (ev.key !== "Enter" || ev.shiftKey || ev.isComposing) return;
  if (isCoarsePointer()) return; // 手机键盘：Enter 换行，用发送按钮
  ev.preventDefault();
  els.composer.requestSubmit();
});

els.input.addEventListener("input", autoGrow);

function autoGrow() {
  els.input.style.height = "auto";
  els.input.style.height = `${Math.min(els.input.scrollHeight, Math.round(window.innerHeight * 0.34))}px`;
}

// ---- 图片附件（按钮/粘贴/拖拽） ----
els.attachBtn.addEventListener("click", () => els.fileInput.click());
els.fileInput.addEventListener("change", () => {
  chat.addFiles(els.fileInput.files);
  els.fileInput.value = "";
});
els.composer.addEventListener("paste", (ev) => {
  const files = ev.clipboardData?.files;
  if (files && files.length > 0) {
    ev.preventDefault();
    chat.addFiles(files);
  }
});
const chatPane = $("chat-pane");
chatPane.addEventListener("dragover", (ev) => {
  if (Array.from(ev.dataTransfer?.types || []).includes("Files")) ev.preventDefault();
});
chatPane.addEventListener("drop", (ev) => {
  const files = ev.dataTransfer?.files;
  if (files && files.length > 0) {
    ev.preventDefault();
    chat.addFiles(files);
  }
});

// ---- 文件面板 ----
els.filesBtn.addEventListener("click", async () => {
  if (activeId === null) return;
  if (els.filesPane.hidden) {
    els.filesBtn.classList.add("active");
    try {
      await files.open(activeId);
    } catch (err) {
      onToast(err.message);
      els.filesBtn.classList.remove("active");
    }
  } else {
    files.close();
    els.filesBtn.classList.remove("active");
  }
});

// ---- 图片全屏预览（聊天气泡 / 历史缩略图 / Markdown 图片通用）----
function openImageViewer(src) {
  if (!src) return;
  els.imageViewerImg.src = src;
  els.imageViewer.hidden = false;
}

function closeImageViewer() {
  els.imageViewer.hidden = true;
  els.imageViewerImg.src = "";
}

els.imageViewerClose.addEventListener("click", closeImageViewer);
els.imageViewer.addEventListener("click", (ev) => {
  // 点击遮罩或图片本身都关闭（图片没有缩放 UI 时不至于"点不动"）
  if (ev.target === els.imageViewer || ev.target === els.imageViewerImg) closeImageViewer();
});
els.messages.addEventListener("click", (ev) => {
  const img = ev.target?.closest?.("img");
  if (img?.src) openImageViewer(img.src);
});

// Esc 关闭浮层
document.addEventListener("keydown", (ev) => {
  if (ev.key !== "Escape") return;
  if (!els.imageViewer.hidden) {
    closeImageViewer();
  } else if (!els.modelSheet.hidden) {
    closeModelSheet();
  } else if (memoryAdmin.isOpen()) {
    memoryAdmin.close();
  } else if (document.body.classList.contains("drawer-open")) {
    closeDrawer();
  }
});

// ---- 启动 ----
(async () => {
  els.input.disabled = true;
  els.sendBtn.disabled = true;
  try {
    const user = await api("/api/me");
    showApp(user);
  } catch {
    showLogin();
  }
})();
