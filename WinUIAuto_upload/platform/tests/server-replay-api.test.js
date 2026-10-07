const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");
const { once } = require("events");
const test = require("node:test");
const { readServerConfig } = require("../lib/server-config");

const rootDirectory = path.resolve(__dirname, "..");

async function waitForServer(process) {
  let output = "";
  const collectOutput = (chunk) => { output += chunk.toString(); };
  process.stdout.on("data", collectOutput);
  try {
    for (let attempt = 0; attempt < 40; attempt += 1) {
      if (process.exitCode !== null) throw new Error("测试服务启动失败");
      const match = /Yuanbao Windows Automation: (http:\/\/localhost:\d+)/.exec(output);
      if (match) {
        try {
          const response = await fetch(`${match[1]}/api/replay/status`);
          if (response.ok) return match[1];
        } catch {
        }
      }
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    throw new Error("等待测试服务启动超时");
  } finally {
    process.stdout.off("data", collectOutput);
  }
}

async function jsonRequest(url, options) {
  const response = await fetch(url, options);
  const body = await response.json();
  if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
  return body;
}

test("server configuration validates ports without enabling remote access", () => {
  assert.equal(readServerConfig({ PORT: "5197" }).port, 5197);
  assert.equal(readServerConfig({ PORT: "0" }).port, 0);
  assert.equal(readServerConfig({ HOST: "0.0.0.0" }).host, "127.0.0.1");
  for (const PORT of ["-1", "65536", "1.5", "abc", " 4173 "]) {
    assert.throws(() => readServerConfig({ PORT }), /PORT must/);
  }
});

test("management server dispatches replay work to an agent", async () => {
  // Run a source-only copy so this test never reads or edits the user's cases.
  const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "cache-agent-api-"));
  for (const entry of ["server.js", "lib", "skills", "index.html", "app.js", "styles.css"]) {
    fs.cpSync(path.join(rootDirectory, entry), path.join(fixtureRoot, entry), { recursive: true });
  }
  const server = spawn(process.execPath, ["server.js"], {
    cwd: fixtureRoot,
    env: {
      ...process.env,
      PORT: "0",
      CACHE_AGENT_DISABLE_REPORT_PERSISTENCE: "1",
      AI_API_KEY: "",
      OPENAI_API_KEY: "",
      AI_API_STYLE: "responses",
    },
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });

  try {
    const baseUrl = await waitForServer(server);
    const emptyCases = await jsonRequest(`${baseUrl}/api/cases`);
    assert.deepEqual(emptyCases.cases, [], "a clean checkout must start without recorded cases");
    const codegen = await jsonRequest(`${baseUrl}/api/codegen/status`);
    assert.equal(codegen.configured, false);
    for (const asset of ["/", "/app.js", "/styles.css"]) {
      const response = await fetch(`${baseUrl}${asset}`);
      assert.equal(response.status, 200, `missing web asset: ${asset}`);
      assert.ok((await response.text()).length > 0);
    }
    fs.writeFileSync(path.join(fixtureRoot, "config.local.json"), JSON.stringify({ apiKey: "" }));
    for (const privatePath of ["/config.local.json", "/server.js", "/lib/server-config.js", "/skills/yuanbao-windows-qta-codegen/SKILL.md"]) {
      const response = await fetch(`${baseUrl}${privatePath}`);
      assert.equal(response.status, 403, `private file exposed: ${privatePath}`);
      await response.text();
    }

    // Minimal synthetic recording, created at test time, never shipped as data.
    const fixtureId = "api-test-case";
    const caseDirectory = path.join(fixtureRoot, "cases-native", fixtureId);
    fs.mkdirSync(caseDirectory, { recursive: true });
    fs.writeFileSync(path.join(caseDirectory, "raw-trace.json"), JSON.stringify({
      schema_version: "0.5-windows-native",
      session_id: fixtureId,
      case_name: "API test fixture",
      target_window: "Test window",
      started_at: "2026-01-01T00:00:00.000Z",
      finished_at: "2026-01-01T00:00:01.000Z",
      actions: [{
        id: "action_0001",
        type: "click",
        timestamp: "2026-01-01T00:00:00.500Z",
        target: { automation_id: "test-button", control_type: "Button" },
        locator: { automation_id: "test-button" },
      }],
    }), "utf8");
    const framesDirectory = path.join(caseDirectory, "frames");
    fs.mkdirSync(framesDirectory);
    fs.writeFileSync(path.join(framesDirectory, "test.png"), Buffer.from([137, 80, 78, 71]));
    const imageResponse = await fetch(`${baseUrl}/cases-native/${fixtureId}/frames/test.png`);
    assert.equal(imageResponse.status, 200, "recorded image evidence must remain accessible");
    assert.equal(imageResponse.headers.get("content-type"), "image/png");
    await imageResponse.arrayBuffer();
    const traceResponse = await fetch(`${baseUrl}/cases-native/${fixtureId}/raw-trace.json`);
    assert.equal(traceResponse.status, 403);
    await traceResponse.text();
    await jsonRequest(`${baseUrl}/api/agents/test-agent/heartbeat`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        status: "idle",
        version: "0.16.0-wait-and-branch-control-flow",
        buildConfiguration: "Current",
        gitCommit: "test-commit",
        capabilities: ["assertions_v1", "aggregate_assertions_v1", "scoped_assertions_v1", "interaction_transactions_v1", "control_flow_v1"],
      }),
    });

    const status = await jsonRequest(`${baseUrl}/api/replay/status`);
    assert.equal(status.available, true);
    assert.equal(status.assertionAvailable, true);

    const cases = await jsonRequest(`${baseUrl}/api/cases`);
    assert.equal(cases.cases.length, 1);
    const replayableCase = cases.cases.find((item) => item.actionCount > 0);
    assert.equal(replayableCase.id, fixtureId);
    const caseId = encodeURIComponent(replayableCase.id);
    const created = await jsonRequest(`${baseUrl}/api/cases/${caseId}/replay`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    assert.equal(created.task.status, "queued");

    const claimed = await jsonRequest(`${baseUrl}/api/agents/test-agent/tasks/claim`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}",
    });
    assert.equal(claimed.task.id, created.task.id);
    assert.ok(claimed.task.actions.length > 0);

    await jsonRequest(`${baseUrl}/api/replay-tasks/${created.task.id}/progress`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        agentId: "test-agent",
        currentStep: 1,
        result: { actionId: claimed.task.actions[0].id, index: 1, status: "succeeded" },
      }),
    });
    await jsonRequest(`${baseUrl}/api/replay-tasks/${created.task.id}/complete`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        agentId: "test-agent",
        success: true,
        completedSteps: claimed.task.actions.length,
        results: [],
      }),
    });

    const final = await jsonRequest(`${baseUrl}/api/replay-tasks/${created.task.id}`);
    assert.equal(final.task.status, "succeeded");
    assert.equal(final.task.currentStep, claimed.task.actions.length);

    const acceptance = await jsonRequest(`${baseUrl}/api/reports/acceptance?limit=10`);
    assert.ok(acceptance.included_case_count > 0);
  } finally {
    if (server.exitCode === null && server.signalCode === null) {
      const stopped = once(server, "exit");
      server.kill();
      await stopped;
    }
    fs.rmSync(fixtureRoot, { recursive: true, force: true });
  }
});
