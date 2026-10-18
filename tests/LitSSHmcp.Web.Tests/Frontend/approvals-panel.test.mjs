// 审批面板测试（node 直跑真实 approavals.js）：快照/新增/关闭、徽标与悬浮面板显隐、收起逻辑、
// REST 对账（漏推自愈）、注销清理。由 ApprovalsPanelTests.cs 经进程调起。
import assert from "node:assert/strict";

const fakeEl = () => ({
  className: "",
  textContent: "",
  hidden: false,
  disabled: false,
  children: [],
  append(...nodes) { this.children.push(...nodes); },
  addEventListener() {},
  replaceChildren(...nodes) { this.children = nodes; },
});

globalThis.document = { cookie: "", createElement: () => fakeEl(), createTextNode: () => ({}) };
globalThis.location = { protocol: "http:", host: "localhost:5000" };
globalThis.WebSocket = class { constructor() { this.readyState = 0; } close() { this.readyState = 3; } send() {} };

let approvalsPayload = { items: [] };
globalThis.fetch = async (path) => {
  if (String(path).includes("/api/approvals")) {
    return { ok: true, status: 200, text: async () => JSON.stringify(approvalsPayload) };
  }
  return { ok: false, status: 401, text: async () => "" };
};

const { ApprovalsPanel } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/approvals.js");

const strip = fakeEl();
const overlay = fakeEl();
const badge = fakeEl();
const count = fakeEl();
const toasts = [];
const panel = new ApprovalsPanel({
  stripEl: strip,
  overlayEl: overlay,
  badgeEl: badge,
  countEl: count,
  onToast: (m) => toasts.push(m),
});

// 1) 快照 → 卡片/计数/徽标/面板可见
panel.handleFrame({
  type: "approval_snapshot",
  items: [{ id: "a", server: "s1", operation: "exec", command: "ls", expiresAt: null }],
});
assert.equal(strip.children.length, 1, "快照应渲染 1 张卡片");
assert.equal(count.textContent, "1", "计数为 1");
assert.equal(badge.hidden, false, "有待审批时徽标可见");
assert.equal(overlay.hidden, false, "面板默认展开");

// 2) 收起 → 面板隐藏但徽标保留；新审批到达自动展开
panel.setCollapsed(true);
assert.equal(overlay.hidden, true, "收起后面板隐藏");
assert.equal(badge.hidden, false, "收起后徽标仍可见");
panel.handleFrame({ type: "approval_request", item: { id: "b", server: "s1", operation: "upload", command: "put" } });
assert.equal(overlay.hidden, false, "新审批到达自动展开");
assert.equal(count.textContent, "2", "自动展开后计数为 2");

// 3) closed 移除对应卡片
panel.handleFrame({ type: "approval_closed", id: "a" });
assert.equal(count.textContent, "1", "closed 后计数减一");

// 4) REST 对账：以服务端为准（漏推/重复推送都能对齐）
approvalsPayload = { items: [{ id: "c", server: "s2", operation: "exec", command: "df" }] };
await panel.reconcile();
assert.equal(count.textContent, "1", "对账后仍 1 项");
assert.ok(panel.pending.has("c"), "对账后包含服务端项");
assert.ok(!panel.pending.has("b"), "对账后移除服务端已不存在的项");

// 5) 注销清理
panel.stop();
assert.equal(count.textContent, "0", "stop 后计数归零");
assert.equal(badge.hidden, true, "stop 后徽标隐藏");
assert.equal(overlay.hidden, true, "stop 后面板隐藏");

console.log("approvals-panel.test.mjs: all passed");
