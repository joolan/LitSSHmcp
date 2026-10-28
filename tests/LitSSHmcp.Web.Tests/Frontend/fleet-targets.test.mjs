import assert from "node:assert/strict";

function fakeEl() {
  const el = {
    textContent: "",
    value: "",
    hidden: false,
    disabled: false,
    checked: false,
    type: "",
    className: "",
    children: [],
    handlers: {},
    append(...nodes) { this.children.push(...nodes); },
    replaceChildren(...nodes) { this.children = nodes; },
    addEventListener(ev, fn) { (this.handlers[ev] ||= []).push(fn); },
  };
  return el;
}

globalThis.document = { cookie: "", createElement: () => fakeEl() };
globalThis.location = { protocol: "http:", host: "localhost" };
globalThis.window = { confirm: () => true };

globalThis.fetch = async (path) => {
  if (String(path).includes("/fleet-targets")) {
    return {
      ok: true,
      status: 200,
      text: async () => JSON.stringify({
        text: JSON.stringify({
          success: true,
          count: 2,
          servers: [
            { id: "s1", name: "web01", host: "10.0.0.1", tags: ["web"] },
            { id: "s2", name: "db01", host: "10.0.0.2", tags: ["db"] },
          ],
        }),
      }),
    };
  }
  return { ok: true, status: 200, text: async () => JSON.stringify({ text: "" }) };
};

const { FleetChangePanel } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/fleet.js");

const elements = {
  sheet: fakeEl(), close: fakeEl(), plan: fakeEl(), dry: fakeEl(), canary: fakeEl(), apply: fakeEl(),
  rollback: fakeEl(), status: fakeEl(), serverIds: fakeEl(), query: fakeEl(), load: fakeEl(),
  clear: fakeEl(), targetList: fakeEl(), targetStatus: fakeEl(), cancel: fakeEl(),
  command: fakeEl(), rollbackCmd: fakeEl(), concurrency: fakeEl(), maxTargets: fakeEl(),
  planId: fakeEl(), progress: fakeEl(), output: fakeEl(),
};

const panel = new FleetChangePanel({
  sheetEl: elements.sheet,
  closeBtn: elements.close,
  buttons: {
    plan: elements.plan,
    dry: elements.dry,
    canary: elements.canary,
    apply: elements.apply,
    rollback: elements.rollback,
    status: elements.status,
  },
  serverIdsEl: elements.serverIds,
  targetQueryEl: elements.query,
  loadTargetsBtn: elements.load,
  clearTargetsBtn: elements.clear,
  targetListEl: elements.targetList,
  targetStatusEl: elements.targetStatus,
  cancelBtn: elements.cancel,
  commandEl: elements.command,
  rollbackEl: elements.rollbackCmd,
  concurrencyEl: elements.concurrency,
  maxTargetsEl: elements.maxTargets,
  planIdEl: elements.planId,
  progressEl: elements.progress,
  outputEl: elements.output,
  getSessionId: () => 42,
  onToast: () => {},
  pollIntervalMs: 0,
});

await panel.loadTargets();
assert.equal(elements.targetList.children.length, 2);
assert.match(elements.targetList.children[0].children[1].textContent, /web01/);

const checkbox = elements.targetList.children[0].children[0];
checkbox.checked = true;
checkbox.handlers.change[0]();
assert.equal(elements.serverIds.value, "s1");

panel.renderStatus({
  phase: "apply",
  running: true,
  targetResults: [
    { serverId: "s1", serverName: "web01", phase: "apply", status: "ok" },
    { serverId: "s2", serverName: "db01", phase: "apply", status: "failed", error: "timeout" },
  ],
});
assert.equal(elements.targetStatus.children.length, 3);
assert.match(elements.targetStatus.children[1].textContent, /OK web01/);
assert.match(elements.targetStatus.children[2].textContent, /ERR db01/);

console.log("fleet-targets.test.mjs passed");
