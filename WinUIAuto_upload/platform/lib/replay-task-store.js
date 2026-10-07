const crypto = require("crypto");

const TERMINAL_STATUSES = new Set(["succeeded", "failed", "cancelled"]);
const REQUIRED_AGENT_VERSION = "0.16.0-wait-and-branch-control-flow";

class ReplayTaskStore {
  constructor({ agentTimeoutMs = 10000, requiredAgentVersion = REQUIRED_AGENT_VERSION } = {}) {
    this.agentTimeoutMs = agentTimeoutMs;
    this.requiredAgentVersion = requiredAgentVersion;
    this.tasks = new Map();
    this.queue = [];
    this.agents = new Map();
  }

  heartbeat(agentId, metadata = {}) {
    const now = new Date().toISOString();
    const version = metadata.version || null;
    const buildConfiguration = metadata.buildConfiguration || null;
    const compatible = version === this.requiredAgentVersion && buildConfiguration === "Current";
    const agent = {
      id: agentId,
      status: metadata.status || "idle",
      version,
      buildConfiguration,
      gitCommit: metadata.gitCommit || null,
      executablePath: metadata.executablePath || null,
      compatible,
      compatibilityReason: compatible
        ? null
        : `需要 ${this.requiredAgentVersion} / Current，当前为 ${version || "未知版本"} / ${buildConfiguration || "未知构建"}`,
      currentTaskId: metadata.currentTaskId || null,
      capabilities: Array.isArray(metadata.capabilities) ? metadata.capabilities.map(String) : [],
      lastSeenAt: now,
    };
    this.agents.set(agentId, agent);
    return agent;
  }

  listAgents() {
    const now = Date.now();
    return [...this.agents.values()].map((agent) => ({
      ...agent,
      online: now - new Date(agent.lastSeenAt).getTime() <= this.agentTimeoutMs,
    }));
  }

  hasOnlineAgent(requiredCapability = null) {
    return this.listAgents().some((agent) =>
      agent.online && agent.compatible && (!requiredCapability || agent.capabilities.includes(requiredCapability)));
  }

  hasActiveTaskForCase(caseId) {
    return [...this.tasks.values()].some((task) =>
      task.caseId === caseId && !TERMINAL_STATUSES.has(task.status));
  }

  createTask(replaySpec, { requiredCapability = null } = {}) {
    const now = new Date().toISOString();
    const task = {
      id: crypto.randomUUID(),
      caseId: replaySpec.caseId,
      caseName: replaySpec.caseName,
      status: "queued",
      createdAt: now,
      claimedAt: null,
      startedAt: null,
      finishedAt: null,
      agentId: null,
      currentStep: 0,
      actionCount: replaySpec.actions.length,
      error: null,
      results: [],
      requiredCapability,
      replaySpec,
    };
    this.tasks.set(task.id, task);
    this.queue.push(task.id);
    return this.toPublicTask(task);
  }

  claim(agentId) {
    const agent = this.agents.get(agentId);
    if (!agent?.compatible) return { task: null };
    for (let index = 0; index < this.queue.length; index += 1) {
      const taskId = this.queue[index];
      const task = this.tasks.get(taskId);
      if (!task || task.status !== "queued") {
        this.queue.splice(index, 1);
        index -= 1;
        continue;
      }
      if (task.requiredCapability && !agent?.capabilities?.includes(task.requiredCapability)) continue;
      this.queue.splice(index, 1);
      const now = new Date().toISOString();
      task.status = "running";
      task.agentId = agentId;
      task.claimedAt = now;
      task.startedAt = now;
      return {
        task: {
          id: task.id,
          case_id: task.caseId,
          case_name: task.caseName,
          target: task.replaySpec.target,
          actions: task.replaySpec.actions,
          assertions: task.replaySpec.assertions || [],
        },
      };
    }
    return { task: null };
  }

  updateProgress(taskId, agentId, progress) {
    const task = this.requireOwnedRunningTask(taskId, agentId);
    task.currentStep = Math.max(0, Number(progress.currentStep || 0));
    if (progress.result) task.results.push(progress.result);
    return this.toPublicTask(task);
  }

  complete(taskId, agentId, result) {
    const task = this.requireOwnedRunningTask(taskId, agentId);
    task.status = result.success ? "succeeded" : "failed";
    task.finishedAt = new Date().toISOString();
    task.currentStep = result.completedSteps ?? task.currentStep;
    task.error = result.error || null;
    if (Array.isArray(result.results)) task.results = result.results;
    return this.toPublicTask(task);
  }

  failOrphanedTasks(agentId, currentTaskId) {
    const failed = [];
    const now = Date.now();
    for (const task of this.tasks.values()) {
      if (task.status !== "running" || task.agentId !== agentId || task.id === currentTaskId) continue;
      const claimedAt = new Date(task.claimedAt || task.startedAt || task.createdAt).getTime();
      if (Number.isFinite(claimedAt) && now - claimedAt < 5000) continue;
      task.status = "failed";
      task.finishedAt = new Date().toISOString();
      task.error = "Agent 已结束本地执行，但未成功上报结果；请查看 Agent 回放日志";
      failed.push(this.toPublicTask(task));
    }
    return failed;
  }

  getTask(taskId) {
    const task = this.tasks.get(taskId);
    if (!task) throw new Error("回放任务不存在");
    return this.toPublicTask(task);
  }

  requireOwnedRunningTask(taskId, agentId) {
    const task = this.tasks.get(taskId);
    if (!task) throw new Error("回放任务不存在");
    if (TERMINAL_STATUSES.has(task.status)) return task;
    if (task.status !== "running" || task.agentId !== agentId) {
      throw new Error("回放任务不属于当前 Agent");
    }
    return task;
  }

  toPublicTask(task) {
    return {
      id: task.id,
      caseId: task.caseId,
      caseName: task.caseName,
      status: task.status,
      createdAt: task.createdAt,
      startedAt: task.startedAt,
      finishedAt: task.finishedAt,
      agentId: task.agentId,
      currentStep: task.currentStep,
      actionCount: task.actionCount,
      error: task.error,
      results: task.results,
      requiredCapability: task.requiredCapability,
      restartBeforeReplay: Boolean(task.replaySpec?.target?.restart_before_replay),
    };
  }
}

module.exports = { ReplayTaskStore, REQUIRED_AGENT_VERSION };
