// sanitize.js 安全测试套件（评估报告 §10.B.3）：node 直接跑，非零退出 = 失败。
// 由 tests/LitSSHmcp.Web.Tests/MarkdownSanitizerTests.cs 经进程调起。
import assert from "node:assert/strict";
import { renderMarkdown, sanitizeUrl, escapeHtml } from "../../../src/LitSSHmcp.Web/wwwroot/js/sanitize.js";

// ---- 构造性安全：原始 HTML 永远进不了输出 ----
{
  const out = renderMarkdown('<script>alert(1)</script>');
  assert.ok(out.includes("&lt;script&gt;"), "script 标签必须被转义");
  assert.ok(!out.includes("<script"), "输出不得含 <script");
}

{
  const out = renderMarkdown('<img src=x onerror=alert(1)>');
  assert.ok(!out.includes("<img"), "原始 img 标签必须被转义");
  assert.ok(out.includes("&lt;img"), "原始 img 应以文本显示");
}

{
  const out = renderMarkdown('<svg/onload=alert(1)>');
  assert.ok(!out.includes("<svg"), "原始 svg 标签必须被转义");
}

// ---- URL scheme 白名单 ----
{
  const out = renderMarkdown("[x](javascript:alert(1))");
  assert.ok(!out.includes("<a "), "javascript: 链接不得生成锚点");
}

{
  const out = renderMarkdown("[click me](javascript:alert(document.cookie))");
  assert.ok(!/href="/.test(out), "javascript: 链接不得生成 href");
}

{
  const out = renderMarkdown("![x](data:text/html,<script>alert(1)</script>)");
  assert.ok(!out.includes("<img"), "data: 图片必须被拒绝");
}

{
  const out = renderMarkdown('[x](vbscript:msgbox)');
  assert.ok(!out.includes("<a "), "vbscript: 链接必须被拒绝");
}

assert.equal(sanitizeUrl("javascript:alert(1)"), null);
assert.equal(sanitizeUrl("JAVASCRIPT:alert(1)"), null);
assert.equal(sanitizeUrl("data:text/html,x"), null);
assert.equal(sanitizeUrl("file:///etc/passwd"), null);
assert.equal(sanitizeUrl("  \thttp://ok.example/a  "), "http://ok.example/a");
assert.equal(sanitizeUrl("mailto:ops@example.com"), "mailto:ops@example.com");
assert.equal(sanitizeUrl(null), null);

// ---- 正常 Markdown 渲染 ----
{
  const out = renderMarkdown("**粗体** 与 *斜体* 与 `代码`");
  assert.ok(out.includes("<strong>粗体</strong>"), "粗体");
  assert.ok(out.includes("<em>斜体</em>"), "斜体");
  assert.ok(out.includes("<code>代码</code>"), "行内代码");
}

{
  const out = renderMarkdown("[面板](https://ops.example/p?a=1&b=2)");
  assert.ok(
    out.includes('href="https://ops.example/p?a=1&amp;b=2"'),
    "合法链接 + & 转义: " + out
  );
  assert.ok(out.includes('rel="noopener noreferrer"'), "外链须带 noopener");
}

{
  const out = renderMarkdown("# 标题一\n\n- 甲\n- 乙\n\n1. 单\n2. 双\n\n> 引用");
  assert.ok(out.includes("<h1>标题一</h1>"), "标题");
  assert.ok(out.includes("<ul>"), "无序列表");
  assert.ok(out.includes("<ol>"), "有序列表");
  assert.ok(out.includes("<blockquote>引用</blockquote>"), "引用");
}

{
  const out = renderMarkdown("```bash\nrm -rf /\n```");
  assert.ok(
    out.includes('<pre><code class="language-bash">rm -rf /</code></pre>'),
    "围栏代码: " + out
  );
}

{
  // 围栏代码里的 HTML 也必须是转义的
  const out = renderMarkdown("```\n<script>alert(1)</script>\n```");
  assert.ok(!out.includes("<script"), "围栏内 HTML 必须转义");
  assert.ok(out.includes("&lt;script&gt;"), "围栏内 HTML 转义显示");
}

{
  // 代码段内不做 Markdown 变换
  const out = renderMarkdown("`**not bold**`");
  assert.ok(out.includes("<code>**not bold**</code>"), "代码内保留字面量: " + out);
  assert.ok(!out.includes("<strong>"), "代码内不渲染强调");
}

// ---- 属性注入面：所有 href/src 值必须过白名单 ----
{
  const nasty = [
    '[a](https://e.com/x)"onmouseover="alert(1)',
    "![a](https://e.com/i.png&quot;onerror=&quot;alert(1))",
    '[b](https://e.com/"onclick="x)',
    "# 标题 <script>bad()</script>",
    "- 甲项 <iframe src=//evil>",
    "> 引用 <body onload=alert(1)>",
    "普通文本 <a href='javascript:1'>x</a>",
  ];
  const urlRe = /(?:href|src)="([^"]*)"/g;
  for (const src of nasty) {
    const out = renderMarkdown(src);
    assert.ok(!out.includes("<script"), "输出不得含 <script: " + src);
    assert.ok(!out.includes("<iframe"), "输出不得含 <iframe: " + src);
    assert.ok(!out.includes("<body"), "输出不得含 <body: " + src);
    let m;
    while ((m = urlRe.exec(out)) !== null) {
      assert.ok(
        /^(?:https?:\/\/|mailto:)/i.test(m[1]),
        `属性值必须过白名单: "${m[1]}" (来自: ${src})`
      );
    }
  }
}

// ---- escapeHtml 基础 ----
assert.equal(escapeHtml(`<>&"'`), "&lt;&gt;&amp;&quot;&#39;");

console.log("sanitize.test.mjs: all assertions passed");
