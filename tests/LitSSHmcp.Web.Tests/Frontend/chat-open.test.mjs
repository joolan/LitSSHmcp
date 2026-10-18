// chat 输入框启用逻辑测试：node 直接跑，非零退出 = 失败。
// 由 tests/LitSSHmcp.Web.Tests/ChatInputTests.cs 经进程调起。
// 背景：open() 先 close()（无会话态强制禁用输入框）再设 sessionId，修复前设置后
// 从未重新启用 → 选中会话也发不出第一条消息、且无任何提示（状态栏却显示"就绪"）。
import assert from "node:assert/strict";

// ---- 浏览器全局打桩（chat.js 经 api.js 触达 document.cookie/fetch/location/WebSocket）----
globalThis.document = { cookie: "" };
globalThis.location = { protocol: "http:", host: "localhost:5000" };
globalThis.fetch = async () => ({ ok: false, status: 401, text: async () => "" });
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

const { ChatController } = await import("../../../src/LitSSHmcp.Web/wwwroot/js/chat.js");

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

console.log("chat-open.test.mjs: all passed");
