// 管理员记忆库前端：列表 / 历史版本 / 回滚 / 归档（node 直跑真实 memory.js）。
import assert from "node:assert/strict";

function fakeEl() {
  return {
    className: "",
    textContent: "",
    hidden: false,
    disabled: false,
    value: "",
    type: "",
    children: [],
    handlers: {},
    append(...nodes) { this.children.push(...nodes); },
    replaceChildren(...nodes) { this.children = nodes; },
    addEventListener(ev, fn) { (this.handlers[ev] ||= []).push(fn); },
    contains() { return true; },
  };
}

globalThis.document = { cookie: "", createElement: () => fakeEl() };
globalThis.location = { protocol: "http:", host: "localhost" };
globalThis.window = { confirm: () => true };

const items = [
  { id: 1, kind: "task", key: "disk", content: "v2 磁盘经验", status: "active", freq: 2, updatedAt: "2026-01-02" },
];
const versions = [
  { version: 1, status: "active", freq: 1, content: "v1 磁盘经验", archivedAt: "2026-01-01" },
];
const calls = [];

globalThis.fetch = async (path, opts = {}) => {
  calls.push({ path: String(path), opts });
  let data = { items };
  if (String(path).includes("/versions")) data = { items: versions };
  else if (String(path).includes("/rollback") || String(path).includes("/archive")) data = { status: "ok" };
  return { ok: true, status: 200, text: async () => JSON.stringify(data) };
};

const { MemoryAdminPanel } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/memory.js");

const sheet = fakeEl();
const list = fakeEl();
const versionsEl = fakeEl();
const kind = fakeEl();
const archiveBtn = fakeEl();
const refreshBtn = fakeEl();
const closeBtn = fakeEl();
const currentEl = fakeEl();
const toasts = [];

const panel = new MemoryAdminPanel({
  sheetEl: sheet,
  listEl: list,
  versionsEl,
  kindEl: kind,
  archiveBtn,
  refreshBtn,
  closeBtn,
  currentEl,
  onToast: (message) => toasts.push(message),
});

// 1) 打开即加载列表
panel.open();
await new Promise((resolve) => setTimeout(resolve, 0));
assert.equal(sheet.hidden, false, "open 后弹层可见");
assert.equal(list.children.length, 1, "应渲染 1 条记忆");
assert.match(list.children[0].children[0].textContent, /task:disk/, "标题包含 kind:key");

// 2) 选中后加载历史版本
await panel.select(items[0]);
assert.equal(currentEl.textContent, "[task:disk]");
assert.equal(versionsEl.children.length, 1, "应渲染 1 个历史版本");
assert.match(versionsEl.children[0].children[0].children[0].textContent, /v1/, "版本标题包含 v1");

// 3) 回滚：POST version 并刷新
await panel.rollback(versions[0]);
const rollbackCall = calls.find((c) => c.path.includes("/rollback"));
assert.ok(rollbackCall, "应调用回滚接口");
assert.equal(JSON.parse(rollbackCall.opts.body).version, 1);
assert.ok(toasts.some((t) => t.includes("已回滚")), "回滚后提示");

// 4) 归档：POST 并清空选中
await panel.archive();
assert.ok(calls.some((c) => c.path.includes("/archive")), "应调用归档接口");
assert.equal(archiveBtn.disabled, true, "归档后按钮禁用");
assert.equal(currentEl.textContent, "", "归档后清空当前标记");

// 5) 关闭
panel.close();
assert.equal(sheet.hidden, true, "close 后弹层隐藏");

console.log("memory-admin.test.mjs: all passed");
