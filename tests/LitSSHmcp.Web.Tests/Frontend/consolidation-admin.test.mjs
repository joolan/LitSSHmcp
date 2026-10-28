// 管理员记忆候选审核前端：列表 / 批准 / 拒绝（node 直跑真实 consolidation.js）。
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
globalThis.window = { confirm: () => true, prompt: () => "重复/无证据" };

const items = [
  {
    id: 1,
    kind: "incident",
    payload: JSON.stringify({ title: "磁盘满恢复", symptom: "df 100%", status: "resolved" }),
    assetIds: "ssh:web01",
    evidence: "raw:1",
    confidence: 0.8,
    status: "pending",
    sourceRefs: "source=会话:s1;raw=1",
    createdAt: "2026-10-26",
    appliedRef: "",
    rejectReason: "",
  },
  {
    id: 2,
    kind: "incident",
    payload: JSON.stringify({ title: "已应用事件", symptom: "test" }),
    assetIds: "ssh:web02",
    evidence: "raw:2",
    confidence: 0.9,
    status: "applied",
    sourceRefs: "source=会话:s2;raw=2",
    createdAt: "2026-10-26",
    appliedRef: "incident:9",
    rejectReason: "",
  },
];
const runs = [
  { id: 11, startedAt: "2026-10-27T10:00:00Z", status: "failed", rawCount: 5, candidateCount: 0, appliedCount: 0, error: "provider timeout" },
  { id: 10, startedAt: "2026-10-27T09:00:00Z", status: "completed", rawCount: 5, candidateCount: 2, appliedCount: 1, error: "" },
];
const calls = [];
globalThis.fetch = async (path, opts = {}) => {
  calls.push({ path: String(path), opts });
  let data = String(path).includes("/runs") ? { items: runs } : { items };
  if (String(path).includes("/approve")) data = { status: "applied", incidentId: 9 };
  else if (String(path).includes("/reject")) data = { status: "rejected" };
  return { ok: true, status: 200, text: async () => JSON.stringify(data) };
};

const { ConsolidationAdminPanel } = await import(
  "../../../src/LitSSHmcp.Web/wwwroot/js/consolidation.js"
);

const sheet = fakeEl();
const list = fakeEl();
const runsEl = fakeEl();
const alertEl = fakeEl();
const status = fakeEl();
const refreshBtn = fakeEl();
const closeBtn = fakeEl();
const toasts = [];

const panel = new ConsolidationAdminPanel({
  sheetEl: sheet,
  listEl: list,
  runsEl,
  alertEl,
  statusEl: status,
  refreshBtn,
  closeBtn,
  onToast: (message) => toasts.push(message),
});

// 1) 打开即加载列表；pending 行应有批准/拒绝按钮，applied 行显示应用结果
panel.open();
await new Promise((resolve) => setTimeout(resolve, 0));
assert.equal(sheet.hidden, false);
assert.equal(list.children.length, 2);
assert.equal(alertEl.hidden, false, "failed run should show alert");
assert.match(alertEl.textContent, /1 次失败/, "alert should contain failure count");
assert.match(alertEl.textContent, /provider timeout/, "alert should contain latest error");
assert.equal(runsEl.children.length, 2, "run history should render two rows");
assert.ok(list.children[0].children.some((c) => c.className === "c-actions"), "pending 行应有操作区");
assert.ok(list.children[1].children.some((c) => c.className === "c-meta" && String(c.textContent).includes("已应用")),
  "applied 行应显示应用结果");

// 2) 批准：POST approve 并刷新
await panel.approve(items[0]);
assert.ok(calls.some((c) => c.path.includes("/candidates/1/approve")), "应调用批准接口");
assert.ok(toasts.some((t) => t.includes("已批准")), "批准后提示");

// 3) 拒绝：POST reject 并带 reason
await panel.reject(items[0]);
const rejectCall = calls.find((c) => c.path.includes("/candidates/1/reject"));
assert.ok(rejectCall, "应调用拒绝接口");
assert.equal(JSON.parse(rejectCall.opts.body).reason, "重复/无证据");
assert.ok(toasts.some((t) => t.includes("已拒绝")), "拒绝后提示");

// 4) 关闭
panel.close();
assert.equal(sheet.hidden, true);

console.log("consolidation-admin.test.mjs: all passed");
