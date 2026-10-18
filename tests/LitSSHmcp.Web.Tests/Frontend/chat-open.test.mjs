// chat 输入框启用逻辑测试：node 直接跑，非零退出 = 失败。
// 由 tests/LitSSHmcp.Web.Tests/ChatInputTests.cs 经进程调起。
// 背景：open() 先 close()（无会话态强制禁用输入框）再设 sessionId，修复前设置后
// 从未重新启用 → 选中会话也发不出第一条消息、且无任何提示（状态栏却显示"就绪"）。
import assert from "node:assert/strict";

// ---- 浏览器全局打桩（chat.js 经 api.js 触达 document.cookie/fetch/location/WebSocket）----
globalThis.document = { cookie: "", createElement: () => el(), createTextNode: () => ({}) };
globalThis.location = { protocol: "http:", host: "localhost:5000" };
let messageRows = [
  { role: "user", content: "旧问" },
  { role: "assistant", content: "旧答" },
];
globalThis.fetch = async (path) => {
  if (String(path).includes("/messages")) {
    return { ok: true, status: 200, text: async () => JSON.stringify(messageRows) };
  }
  return { ok: false, status: 401, text: async () => "" };
};
globalThis.WebSocket = class {
  constructor() {
    this.readyState = 0;
    this.onopen = null;
    this.onclose = null;
    this.onmessage = null;
  }
  close() { this.readyState = 3; }
  send() {}
};

const el = () => ({
  textContent: "",
  innerHTML: "",
  disabled: false,
  scrollTop: 0,
  scrollHeight: 0,
  replaceChildren() {},
  append() {},
  querySelector: () => el(),
});

const { ChatController, parseToolRow } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/chat.js");

// 历史工具轨迹行解析（PascalCase 落库 + camelCase 兼容 + 坏行返回 null）
{
  const step = parseToolRow(JSON.stringify({ Tool: "ssh_list_servers", Args: '{"a":1}', Result: "中文结果", Ok: true, Ms: 12 }));
  assert.equal(step.tool, "ssh_list_servers");
  assert.equal(step.result, "中文结果");
  assert.equal(step.ok, true);
  assert.equal(step.ms, 12);
  const camel = parseToolRow(JSON.stringify({ tool: "x", args: "", result: "", ok: false, ms: 0 }));
  assert.equal(camel.tool, "x");
  assert.equal(camel.ok, false);
  assert.equal(parseToolRow("not-json"), null);
}

const input = { disabled: true };   // 模拟 app.js 启动时的禁用
const sendBtn = { disabled: true };
const stopBtn = { disabled: true };
const chat = new ChatController({
  messagesEl: el(),
  inputEl: input,
  sendBtn,
  stopBtn,
  onToast() {},
  onStatus() {},
});

// 选中会话后必须解禁（open 末尾 setBusy(false)，sessionId 已就位）
await chat.open(1);
assert.equal(input.disabled, false, "open() 后输入框必须可用");
assert.equal(sendBtn.disabled, false, "open() 后发送按钮必须可用");
assert.equal(stopBtn.disabled, true, "未运行时停止按钮保持禁用");
assert.equal(chat.persistedRows, 2, "open() 记录已落库消息数（断线恢复基线）");

// 断线重放：重载历史按库内消息重建（丢失的流式帧无法重发，至少恢复已完成内容）
messageRows = [...messageRows, { role: "assistant", content: "新答" }];
await chat.loadHistory();
assert.equal(chat.persistedRows, 3, "loadHistory() 更新落库基线");

// 运行中禁用、结束后恢复
chat.setBusy(true);
assert.equal(input.disabled, true, "运行中输入框禁用");
assert.equal(sendBtn.disabled, true, "运行中发送按钮禁用");
assert.equal(stopBtn.disabled, false, "运行中停止按钮可用");
chat.setBusy(false);
assert.equal(input.disabled, false, "运行结束输入框恢复");

// 关闭会话（无会话态）回到全禁用，防误发
chat.close();
assert.equal(input.disabled, true, "无会话时输入框禁用");
assert.equal(sendBtn.disabled, true, "无会话时发送按钮禁用");
assert.equal(stopBtn.disabled, true, "无会话时停止按钮禁用");

// ---- 回归：done 前最后一段 assistant_text 不能被节流/reset 清空（"末尾没有结果"）----
{
  const prevCreate = globalThis.document.createElement;
  globalThis.document.createElement = () => {
    const node = {
      className: "",
      textContent: "",
      innerHTML: "",
      disabled: false,
      append() {},
      replaceChildren() {},
      addEventListener() {},
      querySelector() { return node; },
      querySelectorAll() { return []; },
    };
    return node;
  };
  const appended = [];
  const messages = {
    ...el(),
    append(node) { appended.push(node); },
    replaceChildren() { appended.length = 0; },
  };
  const input2 = { disabled: true };
  const chat2 = new ChatController({
    messagesEl: messages,
    inputEl: input2,
    sendBtn: { disabled: true },
    stopBtn: { disabled: true },
    onToast() {},
    onStatus() {},
  });
  chat2.sessionId = 1;
  chat2.dispatch({ type: "assistant_text", text: "最终结论：全部在线" });
  chat2.dispatch({ type: "done" });
  await new Promise((resolve) => setTimeout(resolve, 40));

  const bubble = appended.find((n) => n.className === "bubble assistant");
  assert.ok(bubble, "应创建助手气泡");
  assert.ok(String(bubble.innerHTML).includes("最终结论"), "done 后结论必须保留");
  globalThis.document.createElement = prevCreate;
}

console.log("chat-open.test.mjs: all passed");
