// 运维视图前端：事件列表 / 时间线 / 资产画像（node 直跑真实 ops.js）。
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
    scrollTop: 0,
    append(...nodes) { this.children.push(...nodes); },
    replaceChildren(...nodes) { this.children = nodes; },
    addEventListener(ev, fn) { (this.handlers[ev] ||= []).push(fn); },
    contains() { return true; },
  };
}

globalThis.document = { cookie: "", createElement: () => fakeEl() };
globalThis.location = { protocol: "http:", host: "localhost" };
globalThis.window = {};

const items = [
  {
    id: 1,
    title: "磁盘满恢复",
    status: "resolved",
    assetIds: "ssh:web01",
    rootCause: "journald 膨胀",
    symptom: "df 100%",
    updatedAt: "2026-10-26",
  },
];
const calls = [];
globalThis.fetch = async (path) => {
  calls.push(String(path));
  let data = { items };
  if (String(path).includes("/timeline")) data = { text: "## 时间线\n工具 ssh_snapshot_get 成功" };
  else if (String(path).includes("/asset-profile")) data = { text: "## 资产画像 ssh:web01" };
  return { ok: true, status: 200, text: async () => JSON.stringify(data) };
};

const { OpsInsightsPanel } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/ops.js");

const sheet = fakeEl();
const incidents = fakeEl();
const status = fakeEl();
const refresh = fakeEl();
const close = fakeEl();
const timeline = fakeEl();
const assetId = fakeEl();
const assetLoad = fakeEl();
const profile = fakeEl();
const toasts = [];

const panel = new OpsInsightsPanel({
  sheetEl: sheet,
  incidentsEl: incidents,
  statusEl: status,
  refreshBtn: refresh,
  closeBtn: close,
  timelineEl: timeline,
  assetIdEl: assetId,
  assetLoadBtn: assetLoad,
  profileEl: profile,
  onToast: (message) => toasts.push(message),
});

panel.open();
await new Promise((resolve) => setTimeout(resolve, 0));
assert.equal(sheet.hidden, false);
assert.equal(incidents.children.length, 1);
assert.ok(calls.some((p) => p.includes("/api/ops/incidents")));
assert.ok(calls.some((p) => p.includes("/api/ops/timeline")));
assert.match(timeline.textContent, /ssh_snapshot_get/);

assetId.value = "ssh:web01";
await panel.loadProfile();
assert.ok(calls.some((p) => p.includes("/api/ops/asset-profile")));
assert.match(profile.textContent, /资产画像/);

panel.close();
assert.equal(sheet.hidden, true);
console.log("ops-insights.test.mjs: all passed");
