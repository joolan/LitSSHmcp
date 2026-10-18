// fetch 封装：同源凭据 + CSRF 双提交（X-CSRF-Token ← litssh_csrf cookie，§10.B.1）。

export function csrfCookie() {
  const m = document.cookie.match(/(?:^|;\s*)litssh_csrf=([^;]*)/);
  return m ? decodeURIComponent(m[1]) : "";
}

export async function api(path, { method = "GET", body } = {}) {
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (method !== "GET") {
    const csrf = csrfCookie();
    if (csrf) headers["X-CSRF-Token"] = csrf;
  }
  const resp = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: "same-origin",
  });
  let data = null;
  const text = await resp.text();
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }
  if (!resp.ok) {
    const err = new Error((data && data.error) || `HTTP ${resp.status}`);
    err.status = resp.status;
    err.data = data;
    throw err;
  }
  return data;
}

export function wsUrl(path) {
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${location.host}${path}`;
}

export function downloadUrl(sessionId, relPath) {
  return `/api/sessions/${sessionId}/download?path=${encodeURIComponent(relPath)}`;
}

/** multipart 上传（图片附件）：同样吃 CSRF 双提交头。 */
export async function upload(path, file) {
  const form = new FormData();
  form.append("file", file, file.name || "image.png");
  const headers = {};
  const csrf = csrfCookie();
  if (csrf) headers["X-CSRF-Token"] = csrf;
  const resp = await fetch(path, {
    method: "POST",
    headers,
    body: form,
    credentials: "same-origin",
  });
  let data = null;
  const text = await resp.text();
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }
  if (!resp.ok) {
    const err = new Error((data && data.error) || `HTTP ${resp.status}`);
    err.status = resp.status;
    err.data = data;
    throw err;
  }
  return data;
}
