const assert = require("assert");
const test = require("node:test");
const { ReplayTaskStore } = require("../lib/replay-task-store");

function replaySpec() {
  return {
    caseId: "case-001",
    caseName: "元宝问答",
    target: { process_name: "yuanbao", window_title: "腾讯元宝" },
    actions: [
      {
        id: "action_001",
        type: "click",
        locator: { automation_id: "yuanbao-send-btn" },
        coordinate: { x: 100, y: 200 },
        delay_after_ms: 500,
      },
    ],
    assertions: [],
  };
}

test("dispatches, claims and completes a replay task", () => {
  const store = new ReplayTaskStore();
  store.heartbeat("agent-1", {
    status: "idle",
      version: "0.16.0-wait-and-branch-control-flow",
    buildConfiguration: "Current",
    capabilities: ["assertions_v1"],
  });
  assert.equal(store.hasOnlineAgent(), true);

  const created = store.createTask(replaySpec());
  assert.equal(created.status, "queued");
  assert.equal(created.actionCount, 1);

  const claimed = store.claim("agent-1");
  assert.equal(claimed.task.id, created.id);
  assert.equal(claimed.task.actions[0].locator.automation_id, "yuanbao-send-btn");
  assert.deepEqual(claimed.task.assertions, []);

  const progressed = store.updateProgress(created.id, "agent-1", {
    currentStep: 1,
    result: { actionId: "action_001", index: 1, status: "succeeded" },
  });
  assert.equal(progressed.currentStep, 1);
  assert.equal(progressed.results.length, 1);

  const completed = store.complete(created.id, "agent-1", {
    success: true,
    completedSteps: 1,
  });
  assert.equal(completed.status, "succeeded");
  assert.ok(completed.finishedAt);
});

test("only capable agents can claim assertion tasks", () => {
  const store = new ReplayTaskStore();
  store.heartbeat("old-agent", {
    status: "idle",
    version: "0.16.0-wait-and-branch-control-flow",
    buildConfiguration: "Current",
    capabilities: [],
  });
  store.heartbeat("new-agent", {
    status: "idle",
    version: "0.16.0-wait-and-branch-control-flow",
    buildConfiguration: "Current",
    capabilities: ["assertions_v1"],
  });
  const spec = replaySpec();
  spec.assertions.push({
    id: "assertion_001",
    after_action_id: "action_001",
    type: "target_exists",
  });
  const created = store.createTask(spec, { requiredCapability: "assertions_v1" });
  assert.equal(store.claim("old-agent").task, null);
  const claimed = store.claim("new-agent").task;
  assert.equal(claimed.id, created.id);
  assert.equal(claimed.assertions.length, 1);
});

test("does not expose offline agents as available", () => {
  const store = new ReplayTaskStore({ agentTimeoutMs: -1 });
  store.heartbeat("agent-1", {
    status: "idle",
    version: "0.16.0-wait-and-branch-control-flow",
    buildConfiguration: "Current",
  });
  assert.equal(store.hasOnlineAgent(), false);
});

test("rejects stale or non-current agents", () => {
  const store = new ReplayTaskStore();
  const stale = store.heartbeat("stale-agent", {
    status: "idle",
    version: "0.3.5-transient-menu-assertion",
    buildConfiguration: "Replay",
  });
  assert.equal(stale.compatible, false);
  assert.match(stale.compatibilityReason, /0\.16\.0-wait-and-branch-control-flow/);
  assert.equal(store.hasOnlineAgent(), false);
  const created = store.createTask(replaySpec());
  assert.equal(store.claim("stale-agent").task, null);
  assert.equal(store.getTask(created.id).status, "queued");
});

test("tracks active replay tasks by case", () => {
  const store = new ReplayTaskStore();
  store.heartbeat("agent-1", {
    status: "idle",
    version: "0.16.0-wait-and-branch-control-flow",
    buildConfiguration: "Current",
  });
  const spec = replaySpec();
  const created = store.createTask(spec);
  assert.equal(store.hasActiveTaskForCase(spec.caseId), true);
  store.claim("agent-1");
  store.complete(created.id, "agent-1", { success: true, completedSteps: 1, results: [] });
  assert.equal(store.hasActiveTaskForCase(spec.caseId), false);
});

test("fails a running task when its agent loses local task context", () => {
  const store = new ReplayTaskStore();
  store.heartbeat("agent-1", {
    status: "idle",
    version: "0.16.0-wait-and-branch-control-flow",
    buildConfiguration: "Current",
  });
  const created = store.createTask(replaySpec());
  store.claim("agent-1");
  store.tasks.get(created.id).claimedAt = new Date(Date.now() - 6000).toISOString();
  const failed = store.failOrphanedTasks("agent-1", null);
  assert.equal(failed.length, 1);
  assert.equal(failed[0].id, created.id);
  assert.equal(store.getTask(created.id).status, "failed");
});
