// LitSSHmcp Web — Markdown 渲染 + XSS 防护（评估报告 §10.B.3）。
// 设计原则：先全量转义、再只产出固定标签——模型输出中的原始 HTML 永远进不了产物，
// 不存在 script/事件属性注入面；链接/图片 URL 过 scheme 白名单（http/https/mailto）。
// 无 DOM 依赖，node 可直接测试（tests/LitSSHmcp.Web.Tests/Frontend/sanitize.test.mjs）。

const PH = String.fromCharCode(0); // 行内代码占位符（渲染前已从源文本剔除）

const ESCAPES = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

export function escapeHtml(text) {
  return String(text ?? "").replace(/[&<>"']/g, (ch) => ESCAPES[ch]);
}

// URL 白名单：仅 http(s) / mailto；javascript:/data:/vbscript:/file: 一律拒绝（返回 null）
export function sanitizeUrl(raw) {
  const url = String(raw ?? "").trim();
  if (/^https?:\/\/[^\s<>"']+$/i.test(url)) return url;
  if (/^mailto:[^\s<>"']+$/i.test(url)) return url;
  return null;
}

const IMAGE_RE = /!\[([^\]]*)\]\(([^()\s]+)\)/g;
const LINK_RE = /\[([^\]]+)\]\(([^()\s]+)\)/g;

// 行内渲染（入参必须已 escapeHtml）
function inline(text) {
  // 1. 行内代码先摘出为占位符，内部不再做任何 Markdown 变换
  const codes = [];
  let out = text.replace(/`([^`\n]+)`/g, (_, code) => {
    codes.push(code);
    return PH + String(codes.length - 1) + PH;
  });

  // 2. 图片（仅 http(s)）
  out = out.replace(IMAGE_RE, (m, alt, url) => {
    const u = sanitizeUrl(url);
    if (u === null || !/^https?:/i.test(u)) return m;
    return `<img src="${u}" alt="${alt}" loading="lazy" referrerpolicy="no-referrer">`;
  });

  // 3. 链接（http(s)/mailto；外链新窗口 + noopener）
  out = out.replace(LINK_RE, (m, label, url) => {
    const u = sanitizeUrl(url);
    if (u === null) return m;
    const ext = /^https?:/i.test(u) ? ' target="_blank" rel="noopener noreferrer"' : "";
    return `<a href="${u}"${ext}>${label}</a>`;
  });

  // 4. 强调
  out = out.replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>");
  out = out.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
  out = out.replace(/~~([^~\n]+)~~/g, "<del>$1</del>");

  // 5. 还原代码占位符
  return out.replace(new RegExp(PH + "(\\d+)" + PH, "g"), (_, i) => `<code>${codes[Number(i)]}</code>`);
}

const HEAD_RE = /^(#{1,6})\s+(.*)$/;
const HR_RE = /^(-{3,}|\*{3,}|_{3,})\s*$/;
const QUOTE_RE = /^&gt;\s?(.*)$/;
const UL_RE = /^[-*+]\s+(.*)$/;
const TASK_RE = /^[-*+]\s+\[([ xX])\]\s+(.*)$/;
const OL_RE = /^\d{1,3}\.\s+(.*)$/;
const FENCE_OPEN_RE = /^```(.*)$/;
const BLOCK_START_RE = /^(?:```|#{1,6}\s|>|[-*+]\s|\d{1,3}\.\s|(-{3,}|\*{3,}|_{3,})\s*$)/;

// GFM 表格：拆单元格（去首尾竖线与空白）；不是表格返回 []
function splitTableRow(line) {
  let s = String(line ?? "").trim();
  if (s.startsWith("|")) s = s.slice(1);
  if (s.endsWith("|")) s = s.slice(0, -1);
  if (!s.includes("|")) return [];
  return s.split("|").map((cell) => cell.trim());
}

function isTableSeparator(line) {
  const cells = splitTableRow(line);
  if (cells.length === 0) return false;
  return cells.every((cell) => /^:?-{3,}:?$/.test(cell));
}

// 块级渲染：源文本 → 安全 HTML（全部片段经 escapeHtml + 白名单标签构造）
export function renderMarkdown(source) {
  const text = String(source ?? "").replaceAll(PH, "");
  const lines = text.split(/\r\n|\r|\n/);
  const out = [];
  let i = 0;
  let list = null; // "ul" | "ol" | null

  const closeList = () => {
    if (list !== null) {
      out.push(`</${list}>`);
      list = null;
    }
  };

  while (i < lines.length) {
    const raw = lines[i];

    // 代码围栏：内容整体转义后原样入 pre/code
    const fence = raw.match(FENCE_OPEN_RE);
    if (fence !== null) {
      closeList();
      const lang = (fence[1] || "").trim();
      const buf = [];
      i++;
      while (i < lines.length && lines[i].trim() !== "```") {
        buf.push(lines[i]);
        i++;
      }
      if (i < lines.length) i++; // 跳过收尾围栏
      const cls = /^[\w-]{1,20}$/.test(lang) ? ` class="language-${lang}"` : "";
      out.push(`<pre><code${cls}>${escapeHtml(buf.join("\n"))}</code></pre>`);
      continue;
    }

    if (raw.trim() === "") {
      closeList();
      i++;
      continue;
    }

    const line = escapeHtml(raw);

    let m = raw.match(HEAD_RE);
    if (m !== null) {
      closeList();
      const level = m[1].length;
      out.push(`<h${level}>${inline(escapeHtml(m[2]))}</h${level}>`);
      i++;
      continue;
    }

    if (HR_RE.test(raw)) {
      closeList();
      out.push("<hr>");
      i++;
      continue;
    }

    // GFM 表格（表头 + | --- | 分隔行 + 数据行）：单元格经 escapeHtml + inline 渲染
    if (raw.includes("|") && i + 1 < lines.length && isTableSeparator(lines[i + 1])) {
      const header = splitTableRow(raw);
      if (header.length > 0) {
        closeList();
        i += 2;
        const rows = [];
        while (i < lines.length && lines[i].trim() !== "" && lines[i].includes("|")) {
          const cells = splitTableRow(lines[i]);
          if (cells.length === 0) break;
          rows.push(cells);
          i++;
        }
        const rowHtml = (cells, tag) =>
          `<tr>${cells.map((cell) => `<${tag}>${inline(escapeHtml(cell))}</${tag}>`).join("")}</tr>`;
        out.push(
          `<div class="table-wrap"><table><thead>${rowHtml(header, "th")}</thead><tbody>${rows
            .map((cells) => rowHtml(cells, "td"))
            .join("")}</tbody></table></div>`);
        continue;
      }
    }

    m = line.match(QUOTE_RE);
    if (m !== null) {
      closeList();
      const buf = [m[1]];
      i++;
      while (i < lines.length) {
        const q = escapeHtml(lines[i]).match(QUOTE_RE);
        if (q === null) break;
        buf.push(q[1]);
        i++;
      }
      out.push(`<blockquote>${buf.map(inline).join("<br>")}</blockquote>`);
      continue;
    }

    m = line.match(TASK_RE);
    if (m !== null) {
      if (list !== "ul") {
        closeList();
        out.push('<ul class="tasks">');
        list = "ul";
      }
      const checked = m[1] === " " ? "" : " checked";
      out.push(`<li class="task"><input type="checkbox" disabled${checked}> ${inline(m[2])}</li>`);
      i++;
      continue;
    }

    m = line.match(UL_RE);
    if (m !== null) {
      if (list !== "ul") {
        closeList();
        out.push("<ul>");
        list = "ul";
      }
      out.push(`<li>${inline(m[1])}</li>`);
      i++;
      continue;
    }

    m = line.match(OL_RE);
    if (m !== null) {
      if (list !== "ol") {
        closeList();
        out.push("<ol>");
        list = "ol";
      }
      out.push(`<li>${inline(m[1])}</li>`);
      i++;
      continue;
    }

    // 段落：连续非空非块首行合并，行间 <br>
    closeList();
    const buf = [line];
    i++;
    while (i < lines.length && lines[i].trim() !== "" && !BLOCK_START_RE.test(lines[i])) {
      buf.push(escapeHtml(lines[i]));
      i++;
    }
    out.push(`<p>${buf.map(inline).join("<br>")}</p>`);
  }

  closeList();
  return out.join("\n");
}
