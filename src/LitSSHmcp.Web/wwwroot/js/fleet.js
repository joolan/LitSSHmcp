// 批量变更面板：目标选择 + plan → dry_run → canary → apply → rollback + 实时进度。
// 实际执行由当前会话的 fleet_change 工具完成，写操作仍走会话的 Web 审批通道。

import { api } from "./api.js";

const planIdPattern = /##\s+[^\n]*?计划\s+([^\s]+)/;

export class FleetChangePanel {
  constructor({ sheetEl, closeBtn, buttons, serverIdsEl, targetQueryEl, loadTargetsBtn, clearTargetsBtn,
    targetListEl, targetStatusEl, cancelBtn, commandEl, rollbackEl, concurrencyEl, maxTargetsEl, timeoutEl,
    planIdEl, planSelectEl, deletePlanBtn, progressEl, outputEl, getSessionId, onToast, pollIntervalMs = 1000 }) {
    this.sheetEl = sheetEl;
    this.closeBtn = closeBtn;
    this.buttons = buttons;
    this.serverIdsEl = serverIdsEl;
    this.targetQueryEl = targetQueryEl;
    this.loadTargetsBtn = loadTargetsBtn;
    this.clearTargetsBtn = clearTargetsBtn;
    this.targetListEl = targetListEl;
    this.targetStatusEl = targetStatusEl;
    this.cancelBtn = cancelBtn;
    this.commandEl = commandEl;
    this.rollbackEl = rollbackEl;
    this.concurrencyEl = concurrencyEl;
    this.maxTargetsEl = maxTargetsEl;
    this.timeoutEl = timeoutEl;
    this.planIdEl = planIdEl;
    this.planSelectEl = planSelectEl;
    this.deletePlanBtn = deletePlanBtn;
    this.progressEl = progressEl;
    this.outputEl = outputEl;
    this.getSessionId = getSessionId;
    this.onToast = onToast;
    this.pollIntervalMs = pollIntervalMs;
    this.busy = false;
    this.pollTimer = null;
    this.pollBusy = false;
    this.selected = new Set();

    closeBtn.addEventListener("click", () => this.close());
    buttons.plan.addEventListener("click", () => this.run("plan"));
    buttons.dry.addEventListener("click", () => this.run("dry_run"));
    buttons.canary.addEventListener("click", () => this.run("canary"));
    buttons.apply.addEventListener("click", () => this.run("apply"));
    buttons.rollback.addEventListener("click", () => this.run("rollback"));
    buttons.retry?.addEventListener("click", () => this.run("retry_failed"));
    buttons.status.addEventListener("click", () => this.run("status"));
    loadTargetsBtn?.addEventListener("click", () => this.loadTargets());
    clearTargetsBtn?.addEventListener("click", () => this.clearTargets());
    cancelBtn?.addEventListener("click", () => this.cancel());
    planSelectEl?.addEventListener("change", () => {
      const planId = planSelectEl.value;
      if (!planId) return;
      this.planIdEl.value = planId;
      void this.refreshStatus();
    });
    deletePlanBtn?.addEventListener("click", () => this.deletePlan());
  }

  open() {
    this.sheetEl.hidden = false;
    if (this.getSessionId()) {
      void this.loadTargets();
      void this.loadPlans();
    }
  }

  close() {
    this.stopPolling();
    this.sheetEl.hidden = true;
  }

  setBusy(value) {
    this.busy = value;
    for (const button of Object.values(this.buttons)) button.disabled = value;
    if (this.deletePlanBtn) this.deletePlanBtn.disabled = value;
    this.progressEl.hidden = !value;
    if (this.cancelBtn) {
      this.cancelBtn.hidden = !value;
      this.cancelBtn.disabled = !value;
    }
    if (value) this.progressEl.textContent = "执行中…（如需审批请在审批区处理）";
  }

  append(text) {
    this.outputEl.textContent = text || "(无输出)";
  }

  intValue(el, fallback) {
    const value = Number.parseInt(el.value, 10);
    return Number.isFinite(value) ? value : fallback;
  }

  getServerIds() {
    if (this.selected.size > 0) return Array.from(this.selected).join(", ");
    return this.serverIdsEl.value.trim();
  }

  async loadPlans() {
    const sessionId = this.getSessionId();
    if (!sessionId || !this.planSelectEl) return;
    try {
      const plans = await api(`/api/sessions/${sessionId}/fleet-plans`);
      const current = this.planIdEl.value.trim();
      this.planSelectEl.replaceChildren();
      const blank = document.createElement("option");
      blank.value = "";
      blank.textContent = "最近计划…";
      this.planSelectEl.append(blank);
      for (const plan of plans ?? []) {
        const option = document.createElement("option");
        option.value = plan.id;
        const count = Number.isFinite(plan.targetCount) ? plan.targetCount
          : (Array.isArray(plan.targets) ? plan.targets.length : 0);
        option.textContent = `${plan.id} · ${plan.phase} · ${count} 台`;
        if (plan.id === current) option.selected = true;
        this.planSelectEl.append(option);
      }
    } catch {
      // 最近计划加载失败不影响手工重新生成
    }
  }

  async deletePlan() {
    const sessionId = this.getSessionId();
    const planId = this.planIdEl.value.trim();
    if (!sessionId || !planId) return;
    if (!window.confirm(`删除计划 ${planId}？（不影响已执行目标）`)) return;
    try {
      await api(`/api/sessions/${sessionId}/fleet-change/${encodeURIComponent(planId)}`,
        { method: "DELETE" });
      this.planIdEl.value = "";
      this.targetStatusEl.replaceChildren();
      this.outputEl.textContent = "计划已删除。";
      await this.loadPlans();
    } catch (err) {
      this.onToast(`删除计划失败：${err.message || err}`);
    }
  }

  async loadTargets() {
    const sessionId = this.getSessionId();
    if (!sessionId) return;
    const query = this.targetQueryEl?.value?.trim() ?? "";
    this.targetListEl.textContent = "加载中…";
    try {
      const result = await api(`/api/sessions/${sessionId}/fleet-targets?query=${encodeURIComponent(query)}`);
      const parsed = parseJson(result?.text);
      if (!parsed) {
        this.targetListEl.textContent = result?.text || "目标清单为空或不可用";
        return;
      }
      const servers = Array.isArray(parsed.servers) ? parsed.servers : [];
      this.renderTargets(servers);
    } catch (err) {
      this.targetListEl.textContent = `加载失败：${err.message || err}`;
    }
  }

  renderTargets(servers) {
    this.targetListEl.replaceChildren();
    for (const server of servers) {
      const id = String(server.id ?? "");
      if (!id) continue;
      const row = document.createElement("label");
      row.className = "fleet-target-item";
      const check = document.createElement("input");
      check.type = "checkbox";
      check.value = id;
      check.checked = this.selected.has(id);
      check.addEventListener("change", () => {
        if (check.checked) this.selected.add(id);
        else this.selected.delete(id);
        this.serverIdsEl.value = Array.from(this.selected).join(", ");
      });
      const text = document.createElement("span");
      const meta = [server.name, server.host, ...(Array.isArray(server.tags) ? server.tags : [])]
        .filter(Boolean).join(" · ");
      text.textContent = `${meta || id}（${id}）`;
      if (server.canExec === false) {
        check.disabled = true;
        text.textContent += "（无 exec 权限）";
      }
      row.append(check, text);
      this.targetListEl.append(row);
    }
    if (servers.length === 0) this.targetListEl.textContent = "没有匹配的服务器";
  }

  clearTargets() {
    this.selected.clear();
    this.serverIdsEl.value = "";
    for (const row of this.targetListEl.children ?? []) {
      const check = row.children?.[0];
      if (check) check.checked = false;
    }
  }

  startPolling() {
    this.stopPolling();
    if (!this.pollIntervalMs || this.pollIntervalMs <= 0) return;
    this.pollTimer = setInterval(() => void this.refreshStatus(), this.pollIntervalMs);
    void this.refreshStatus();
  }

  stopPolling() {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  async refreshStatus() {
    if (this.pollBusy) return;
    const sessionId = this.getSessionId();
    const planId = this.planIdEl.value.trim();
    if (!sessionId || !planId || !this.targetStatusEl) return;
    this.pollBusy = true;
    try {
      const plan = await api(`/api/sessions/${sessionId}/fleet-change/${encodeURIComponent(planId)}`);
      if (plan) this.renderStatus(plan);
    } catch {
      // 计划可能刚创建或已过期，下一次轮询重试
    } finally {
      this.pollBusy = false;
    }
  }

  renderStatus(plan) {
    const targets = Array.isArray(plan.targetResults) ? plan.targetResults : [];
    const done = targets.filter((t) => ["ok", "failed", "cancelled"].includes(t.status)).length;
    const summary = document.createElement("div");
    summary.className = "fleet-target-summary";
    summary.textContent = `阶段 ${plan.phase || "-"} · 进度 ${done}/${targets.length}` +
      (plan.running ? " · 运行中" : "");
    this.targetStatusEl.replaceChildren(summary);
    for (const target of targets) {
      const row = document.createElement("div");
      row.className = `fleet-target-row ${target.status || ""}`;
      const name = target.serverName || target.serverId;
      row.textContent = `${statusLabel(target.status)} ${name} [${target.phase || "-"}]` +
        (target.error ? `：${target.error}` : "");
      this.targetStatusEl.append(row);
    }
    if (plan.running) this.progressEl.textContent = `执行中：${plan.phase || ""}（${done}/${targets.length}）`;
  }

  async cancel() {
    const sessionId = this.getSessionId();
    if (!sessionId) return;
    if (!window.confirm("确定取消当前批量变更？已执行的目标不会自动回滚。")) return;
    try {
      await api(`/api/sessions/${sessionId}/fleet-change/cancel`, { method: "POST", body: {} });
      this.progressEl.textContent = "已发送取消请求，等待当前目标结束…";
    } catch (err) {
      this.onToast(`取消失败：${err.message || err}`);
    }
  }

  async run(mode) {
    if (this.busy) return;
    const sessionId = this.getSessionId();
    if (!sessionId) {
      this.onToast("请先打开一个会话");
      return;
    }
    const planId = this.planIdEl.value.trim();
    if (mode !== "plan" && planId.length === 0) {
      this.onToast("请先生成计划，或填写计划 ID");
      return;
    }
    if (mode === "canary" && !window.confirm("金丝雀会在 1 台目标上执行变更命令，继续？")) return;
    if (mode === "apply" && !window.confirm("全量 apply 会对剩余目标执行变更，确认 canary 已通过？")) return;
    if (mode === "rollback" && !window.confirm("回滚会对已变更目标执行回滚命令，继续？")) return;
    if (mode === "retry_failed" && !window.confirm("仅重试失败目标，继续？")) return;

    const body = {
      mode,
      planId: planId || null,
      serverIds: this.getServerIds(),
      command: this.commandEl.value.trim(),
      rollbackCommand: this.rollbackEl.value.trim(),
      concurrency: this.intValue(this.concurrencyEl, 4),
      maxTargets: this.intValue(this.maxTargetsEl, 20),
      timeoutSeconds: this.intValue(this.timeoutEl, 60),
      confirm: mode === "canary" || mode === "apply" || mode === "rollback" || mode === "retry_failed",
    };

    this.setBusy(true);
    this.outputEl.textContent = "执行中…";
    if (planId) this.startPolling();
    try {
      const result = await api(`/api/sessions/${sessionId}/fleet-change`, { method: "POST", body });
      const text = (result && result.text) || "";
      this.append(text);
      if (mode === "plan") {
        const match = planIdPattern.exec(text);
        if (match) {
          this.planIdEl.value = match[1];
          void this.loadPlans();
          this.startPolling();
        }
      }
      if (this.pollIntervalMs > 0) await this.refreshStatus();
    } catch (err) {
      this.append(`失败：${err.message || err}`);
      this.onToast(`批量变更失败：${err.message || err}`);
    } finally {
      this.stopPolling();
      this.setBusy(false);
    }
  }
}

function parseJson(text) {
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function statusLabel(status) {
  switch (status) {
    case "ok": return "OK";
    case "failed": return "ERR";
    case "running": return "…";
    case "cancelled": return "CANCEL";
    default: return "PENDING";
  }
}
