const http = require("http");
const fs = require("fs");
const path = require("path");
const { NativeCaseStore } = require("./lib/native-case-store");
const { ReplayTaskStore } = require("./lib/replay-task-store");
const { QtaCodeGenerator } = require("./lib/qta-code-generator");
const { readServerConfig, serviceUrl } = require("./lib/server-config");

const rootDirectory = __dirname;
const { host, port } = readServerConfig();
const caseStore = new NativeCaseStore({ rootDirectory });
const replayTaskStore = new ReplayTaskStore();
const qtaCodeGenerator = new QtaCodeGenerator({ rootDirectory });
const replayReportPersistenceEnabled = process.env.CACHE_AGENT_DISABLE_REPORT_PERSISTENCE !== "1";

const mimeTypes = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "application/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".py": "text/plain; charset=utf-8",
  ".svg": "image/svg+xml; charset=utf-8",
  ".png": "image/png",
};

function sendJson(response, statusCode, payload) {
  response.writeHead(statusCode, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
  });
  response.end(`${JSON.stringify(payload)}\n`);
}

function readJsonBody(request) {
  return new Promise((resolve, reject) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => {
      body += chunk;
      if (body.length > 1024 * 1024) {
        reject(new Error("请求体过大"));
        request.destroy();
      }
    });
    request.on("end", () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch {
        reject(new Error("请求 JSON 格式不正确"));
      }
    });
    request.on("error", reject);
  });
}

async function handleApi(request, response, url) {
  if (request.method === "GET" && url.pathname === "/api/codegen/status") {
    sendJson(response, 200, qtaCodeGenerator.status());
    return true;
  }

  if (request.method === "GET" && url.pathname === "/api/replay/status") {
    const agents = replayTaskStore.listAgents();
    sendJson(response, 200, {
      available: agents.some((agent) => agent.online && agent.compatible),
      assertionAvailable: agents.some((agent) => agent.online && agent.compatible && agent.capabilities.includes("assertions_v1")),
      agents,
    });
    return true;
  }

  const heartbeatRoute = /^\/api\/agents\/([^/]+)\/heartbeat$/.exec(url.pathname);
  if (request.method === "POST" && heartbeatRoute) {
    const agentId = decodeURIComponent(heartbeatRoute[1]);
    const body = await readJsonBody(request);
    const agent = replayTaskStore.heartbeat(agentId, body);
    const orphanedTasks = replayTaskStore.failOrphanedTasks(agentId, body.currentTaskId || null);
    for (const task of orphanedTasks) {
      if (replayReportPersistenceEnabled) caseStore.saveReplayReport(task.caseId, task);
      console.log(`[replay:orphaned] task=${task.id} agent=${agentId}`);
    }
    sendJson(response, 200, { agent, orphanedTasks });
    return true;
  }

  const claimRoute = /^\/api\/agents\/([^/]+)\/tasks\/claim$/.exec(url.pathname);
  if (request.method === "POST" && claimRoute) {
    const agentId = decodeURIComponent(claimRoute[1]);
    sendJson(response, 200, replayTaskStore.claim(agentId));
    return true;
  }

  const progressRoute = /^\/api\/replay-tasks\/([^/]+)\/progress$/.exec(url.pathname);
  if (request.method === "POST" && progressRoute) {
    const taskId = decodeURIComponent(progressRoute[1]);
    const body = await readJsonBody(request);
    sendJson(response, 200, {
      task: replayTaskStore.updateProgress(taskId, body.agentId, body),
    });
    return true;
  }

  const completeRoute = /^\/api\/replay-tasks\/([^/]+)\/complete$/.exec(url.pathname);
  if (request.method === "POST" && completeRoute) {
    const taskId = decodeURIComponent(completeRoute[1]);
    const body = await readJsonBody(request);
    const task = replayTaskStore.complete(taskId, body.agentId, body);
    if (replayReportPersistenceEnabled) caseStore.saveReplayReport(task.caseId, task);
    sendJson(response, 200, { task });
    return true;
  }

  const taskRoute = /^\/api\/replay-tasks\/([^/]+)$/.exec(url.pathname);
  if (request.method === "GET" && taskRoute) {
    const taskId = decodeURIComponent(taskRoute[1]);
    sendJson(response, 200, { task: replayTaskStore.getTask(taskId) });
    return true;
  }

  if (request.method === "GET" && url.pathname === "/api/cases") {
    sendJson(response, 200, { cases: caseStore.listCases(), activeSession: null });
    return true;
  }

  if (request.method === "GET" && url.pathname === "/api/reports/acceptance") {
    const limit = Number(url.searchParams.get("limit") || 10);
    sendJson(response, 200, caseStore.buildAcceptanceReport({ limit }));
    return true;
  }

  const latestReportRoute = /^\/api\/cases\/([^/]+)\/reports\/latest$/.exec(url.pathname);
  if (request.method === "GET" && latestReportRoute) {
    const caseId = decodeURIComponent(latestReportRoute[1]);
    const report = caseStore.loadLatestReplayReport(caseId);
    if (!report) {
      sendJson(response, 404, { error: "当前用例还没有回放报告" });
      return true;
    }
    sendJson(response, 200, { report });
    return true;
  }

  const actionsRoute = /^\/api\/cases\/([^/]+)\/actions$/.exec(url.pathname);
  if (request.method === "PUT" && actionsRoute) {
    const caseId = decodeURIComponent(actionsRoute[1]);
    const body = await readJsonBody(request);
    sendJson(response, 200, caseStore.saveReviewActions(caseId, body.actions));
    return true;
  }

  const assertionsRoute = /^\/api\/cases\/([^/]+)\/assertions$/.exec(url.pathname);
  if (request.method === "PUT" && assertionsRoute) {
    const caseId = decodeURIComponent(assertionsRoute[1]);
    const body = await readJsonBody(request);
    sendJson(response, 200, caseStore.saveReviewAssertions(caseId, body.assertions));
    return true;
  }

  const caseRoute = /^\/api\/cases\/([^/]+)$/.exec(url.pathname);
  if ((request.method === "PATCH" || request.method === "DELETE") && caseRoute) {
    const caseId = decodeURIComponent(caseRoute[1]);
    if (replayTaskStore.hasActiveTaskForCase(caseId)) {
      sendJson(response, 409, { error: "当前用例正在等待或执行回放，暂时不能修改" });
      return true;
    }
    if (request.method === "PATCH") {
      const body = await readJsonBody(request);
      sendJson(response, 200, caseStore.renameCase(caseId, body.name));
    } else {
      sendJson(response, 200, caseStore.deleteCase(caseId));
    }
    return true;
  }

  const replayRoute = /^\/api\/cases\/([^/]+)\/replay$/.exec(url.pathname);
  if (request.method === "POST" && replayRoute) {
    if (!replayTaskStore.hasOnlineAgent()) {
      sendJson(response, 409, { error: "未检测到在线的原生 Agent，请先启动 YuanbaoRecorder.Agent" });
      return true;
    }
    const caseId = decodeURIComponent(replayRoute[1]);
    const body = await readJsonBody(request);
    const replaySpec = caseStore.buildReplaySpec(caseId, {
      restartBeforeReplay: Boolean(body.restartBeforeReplay),
    });
    if (replaySpec.actions.length === 0) {
      sendJson(response, 400, { error: "当前用例没有可回放动作" });
      return true;
    }
    const hasAggregateAssertions = replaySpec.assertions.some((assertion) =>
      ["keywords_match_count", "descendant_count", "table_dimensions", "horizontal_bounds_within_window", "descendants_within_bounds"].includes(assertion.type));
    const hasControlFlow = replaySpec.actions.some((action) => ["wait_for_target", "condition"].includes(action.type));
    const hasAdvancedInteractions = replaySpec.target.restart_before_replay ||
      replaySpec.actions.some((action) => ["scroll", "drag"].includes(action.type));
    const requiredCapability = hasControlFlow
      ? "control_flow_v1"
      : hasAdvancedInteractions ? "interaction_transactions_v1"
      : hasAggregateAssertions ? "scoped_assertions_v1"
        : replaySpec.assertions.length > 0 ? "assertions_v1" : null;
    if (requiredCapability && !replayTaskStore.hasOnlineAgent(requiredCapability)) {
      sendJson(response, 409, { error: "当前在线 Agent 不支持该用例所需能力，请启动最新版本" });
      return true;
    }
    const task = replayTaskStore.createTask(replaySpec, { requiredCapability });
    console.log(`[replay:create] task=${task.id} case=${caseId} actions=${replaySpec.actions.length} restart=${replaySpec.target.restart_before_replay}`);
    sendJson(response, 202, { task });
    return true;
  }

  const codegenRoute = /^\/api\/cases\/([^/]+)\/generate-qta$/.exec(url.pathname);
  if (codegenRoute) {
    const caseId = decodeURIComponent(codegenRoute[1]);
    if (request.method === "GET") {
      const generated = qtaCodeGenerator.load(caseId);
      if (!generated) {
        sendJson(response, 404, { error: "当前用例还没有生成 QTA 代码" });
        return true;
      }
      sendJson(response, 200, { generated });
      return true;
    }
    if (request.method === "POST") {
      const body = await readJsonBody(request);
      const loaded = caseStore.loadCase(caseId);
      const generated = await qtaCodeGenerator.generate(caseId, loaded.cacheCase, { owner: body.owner });
      sendJson(response, 201, { generated });
      return true;
    }
  }

  const exportQtaRoute = /^\/api\/cases\/([^/]+)\/export-qta$/.exec(url.pathname);
  if (exportQtaRoute) {
    if (request.method !== "POST") {
      sendJson(response, 405, { error: "仅支持 POST 导出 QTA 代码" });
      return true;
    }
    const caseId = decodeURIComponent(exportQtaRoute[1]);
    const exported = qtaCodeGenerator.export(caseId);
    sendJson(response, 201, { exported });
    return true;
  }

  if (request.method === "GET" && url.pathname.startsWith("/api/cases/")) {
    const caseId = decodeURIComponent(url.pathname.slice("/api/cases/".length));
    sendJson(response, 200, { ...caseStore.loadCase(caseId), generatedQta: qtaCodeGenerator.load(caseId) });
    return true;
  }

  if (url.pathname.startsWith("/api/recordings/") || url.pathname === "/api/windows/live") {
    sendJson(response, 410, { error: "网页录制链路已停用，请使用 YuanbaoRecorder.Agent 原生录制器" });
    return true;
  }
  return false;
}

function resolveRequestPath(pathname) {
  const relativePath = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
  // Only the browser bundle and recorded image evidence are public files.
  // Never serve source code, local API credentials or generation diagnostics.
  const publicAssets = new Set(["index.html", "app.js", "styles.css"]);
  const isFrame = /^cases-native\/[^/\\]+\/frames\/[^/\\]+\.png$/i.test(relativePath);
  if (!publicAssets.has(relativePath) && !isFrame) return null;
  const resolvedPath = path.resolve(rootDirectory, relativePath);
  const relativeToRoot = path.relative(rootDirectory, resolvedPath);
  if (relativeToRoot.startsWith("..") || path.isAbsolute(relativeToRoot)) return null;
  return resolvedPath;
}

const server = http.createServer(async (request, response) => {
  const url = new URL(request.url, "http://localhost");
  try {
    if (url.pathname.startsWith("/api/")) {
      const handled = await handleApi(request, response, url);
      if (!handled) sendJson(response, 404, { error: "API 不存在" });
      return;
    }

    const filePath = resolveRequestPath(decodeURIComponent(url.pathname));
    if (!filePath) {
      response.writeHead(403);
      response.end("Forbidden");
      return;
    }
    fs.readFile(filePath, (error, content) => {
      if (error) {
        response.writeHead(error.code === "ENOENT" ? 404 : 500);
        response.end(error.code === "ENOENT" ? "Not Found" : "Internal Server Error");
        return;
      }
      response.writeHead(200, {
        "Content-Type": mimeTypes[path.extname(filePath).toLowerCase()] || "application/octet-stream",
        "Cache-Control": "no-store",
      });
      response.end(content);
    });
  } catch (error) {
    console.error(error);
    if (!response.headersSent) sendJson(response, 400, { error: error.message });
  }
});

server.on("error", (error) => {
  console.error(`Management server failed to start: ${error.message}`);
  process.exitCode = 1;
});

server.listen(port, host, () => {
  console.log(`Yuanbao Windows Automation: ${serviceUrl(server.address().port)}`);
  console.log("Native case review API is ready.");
});
