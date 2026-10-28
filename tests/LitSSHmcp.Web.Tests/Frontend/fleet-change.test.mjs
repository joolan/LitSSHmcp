import assert from "node:assert/strict";

function fakeEl() {
  return {
    textContent: "",
    value: "",
    hidden: false,
    disabled: false,
    handlers: {},
    addEventListener(ev, fn) { (this.handlers[ev] ||= []).push(fn); },
  };
}

globalThis.document = { cookie: "", createElement: () => fakeEl() };
globalThis.location = { protocol: "http:", host: "localhost" };
globalThis.window = { confirm: () => true };

const requests = [];
globalThis.fetch = async (path, options) => {
  requests.push({ path: String(path), body: JSON.parse(options.body) });
  const body = JSON.parse(options.body);
  const text = body.mode === "plan"
    ? "## 批量变更计划 plan-abc\n- 目标: 2 台"
    : "## canary 成功 plan-abc";
  return {
    ok: true,
    status: 200,
    text: async () => JSON.stringify({ text }),
  };
};

const { FleetChangePanel } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/fleet.js");

const elements = {
  sheet: fakeEl(),
  close: fakeEl(),
  plan: fakeEl(),
  dry: fakeEl(),
  canary: fakeEl(),
  apply: fakeEl(),
  rollback: fakeEl(),
  status: fakeEl(),
  serverIds: fakeEl(),
  command: fakeEl(),
  rollbackCmd: fakeEl(),
  concurrency: fakeEl(),
  maxTargets: fakeEl(),
  timeout: fakeEl(),
  planId: fakeEl(),
  progress: fakeEl(),
  output: fakeEl(),
};
elements.serverIds.value = "web01,web02";
elements.command.value = "systemctl restart nginx";
elements.rollbackCmd.value = "systemctl start nginx.prev";
elements.concurrency.value = "3";
elements.maxTargets.value = "10";
elements.timeout.value = "90";

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
  commandEl: elements.command,
  rollbackEl: elements.rollbackCmd,
  concurrencyEl: elements.concurrency,
  maxTargetsEl: elements.maxTargets,
  timeoutEl: elements.timeout,
  planIdEl: elements.planId,
  progressEl: elements.progress,
  outputEl: elements.output,
  getSessionId: () => 42,
  onToast: () => {},
  pollIntervalMs: 0,
});

assert.equal(elements.plan.handlers.click.length, 1);
elements.plan.handlers.click[0]();
await new Promise((r) => setTimeout(r, 0));
assert.equal(requests[0].path, "/api/sessions/42/fleet-change");
assert.equal(requests[0].body.mode, "plan");
assert.equal(requests[0].body.serverIds, "web01,web02");
assert.equal(requests[0].body.concurrency, 3);
assert.equal(requests[0].body.timeoutSeconds, 90);
assert.equal(elements.planId.value, "plan-abc");
assert.match(elements.output.textContent, /计划 plan-abc/);

elements.canary.handlers.click[0]();
await new Promise((r) => setTimeout(r, 0));
assert.equal(requests[1].body.mode, "canary");
assert.equal(requests[1].body.planId, "plan-abc");
assert.equal(requests[1].body.confirm, true);

console.log("fleet-change.test.mjs passed");
