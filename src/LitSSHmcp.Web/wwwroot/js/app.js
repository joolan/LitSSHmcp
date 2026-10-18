// 应用入口：登录/会话列表/视图接线（聊天、审批、目录由各模块接管）。
import { api } from "./api.js";
import { ChatController } from "./chat.js";
import { ApprovalsPanel } from "./approvals.js";
import { FilesPanel } from "./files.js";

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
  newSessionBtn: $("new-session-btn"),
  sessionList: $("session-list"),
  sessionTitle: $("session-title"),
  turnStatus: $("turn-status"),
  filesBtn: $("files-btn"),
  stopBtn: $("stop-btn"),
  approvalStrip: $("approval-strip"),
  approvalOverlay: $("approval-overlay"),
  approvalBadge: $("approval-badge"),
  approvalCount: $("approval-count"),
  approvalCollapse: $("approval-collapse"),
  messages: $("messages"),
  composer: $("composer"),
  input: $("input"),
  sendBtn: $("send-btn"),
  filesPane: $("files-pane"),
  filesPath: $("files-path"),
  filesList: $("files-list"),
  filesUp: $("files-up"),
  filesClose: $("files-close"),
  toast: $("toast"),
};

let me = null;
let sessions = [];
let activeId = null;
let toastTimer = null;

const chat = new ChatController({
  messagesEl: els.messages,
  inputEl: els.input,
  sendBtn: els.sendBtn,
  stopBtn: els.stopBtn,
  onToast,
  onStatus: (text) => {
    els.turnStatus.textContent = text;
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
  onOpened: () => {
    document.querySelector(".layout").classList.add("files-open");
  },
  onClosed: () => {
    document.querySelector(".layout").classList.remove("files-open");
    els.filesBtn.classList.remove("active");
  },
});

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

// ---- 视图切换 ----
function showLogin() {
  els.appView.hidden = true;
  els.loginView.hidden = false;
  els.loginUser.focus();
  updatePlaceholder();
}

function showApp(user) {
  me = user;
  els.loginView.hidden = true;
  els.appView.hidden = false;
  els.whoami.textContent = `${user.username}（${user.role}）`;
  updatePlaceholder();
  approvals.start();
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
  me = null;
  sessions = [];
  activeId = null;
  els.sessionList.replaceChildren();
  els.messages.replaceChildren();
  showLogin();
});

// ---- 会话列表 ----
async function loadSessions() {
  sessions = await api("/api/sessions");
  renderSessions();
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
    time.textContent = s.updatedAt ? new Date(s.updatedAt).toLocaleString() : "";
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
}

// ---- 聊天输入 ----
els.composer.addEventListener("submit", (ev) => {
  ev.preventDefault();
  const text = els.input.value.trim();
  if (!text || chat.busy) return;
  els.input.value = "";
  chat.send(text);
});

els.input.addEventListener("keydown", (ev) => {
  if (ev.key === "Enter" && !ev.shiftKey) {
    ev.preventDefault();
    els.composer.requestSubmit();
  }
});

els.stopBtn.addEventListener("click", () => chat.stop());

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
