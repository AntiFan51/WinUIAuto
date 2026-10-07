const fs = require("fs");
const path = require("path");
const { NativeCaseStore } = require("../lib/native-case-store");

const rootDirectory = path.resolve(__dirname, "..");
const limit = Math.max(1, Math.min(100, Number(process.argv[2] || 10)));
const store = new NativeCaseStore({ rootDirectory });
const report = store.buildAcceptanceReport({ limit });
const reportsDirectory = path.join(rootDirectory, "runtime", "reports");
fs.mkdirSync(reportsDirectory, { recursive: true });

const jsonPath = path.join(reportsDirectory, "windows-acceptance-report.json");
const markdownPath = path.join(reportsDirectory, "windows-acceptance-report.md");
fs.writeFileSync(jsonPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");

const lines = [
  "# 元宝 Windows 人工测试经验沉淀平台验收报告",
  "",
  `生成时间：${report.generated_at}`,
  "",
  "## 汇总",
  "",
  `- 纳入用例：${report.included_case_count}/${report.requested_case_count}`,
  `- 录制完成：${report.summary.capture_completed}`,
  `- Cache Case 已生成：${report.summary.cache_generated}`,
  `- 已执行回放：${report.summary.replayed}`,
  `- 回放成功：${report.summary.replay_succeeded}`,
  `- 回放失败：${report.summary.replay_failed}`,
  `- 待人工复核步骤：${report.summary.needs_review}`,
  `- 已生成 QTA：${report.summary.generated_qta}`,
  `- 已过期 QTA：${report.summary.generated_qta_stale}`,
  `- 可进入 QTA 实跑：${report.summary.ready_for_qta_run}`,
  "",
  "## 用例明细",
  "",
  "| 序号 | 模块 | 用例 | 动作 | 断言 | 录制 | Cache | 回放 | QTA代码 | 可实跑 | 错误 |",
  "|---:|---|---|---:|---:|---|---|---|---|---|---|",
];
report.cases.forEach((item, index) => {
  const safe = (value) => String(value ?? "").replace(/\|/g, "\\|").replace(/[\r\n]+/g, " ");
  lines.push(`| ${index + 1} | ${safe(item.module)} | ${safe(item.case_name)} | ${item.action_count} | ${item.assertion_count} | ${safe(item.capture_status)} | ${safe(item.cache_status)} | ${safe(item.replay_status)} | ${safe(item.generated_qta_status)} | ${item.ready_for_qta_run ? "yes" : "no"} | ${safe(item.replay_error)} |`);
});
lines.push("", "> `not_run` 表示尚未执行回放，不计为成功。未设置业务模块的用例显示为“未分类”。", "");
fs.writeFileSync(markdownPath, lines.join("\n"), "utf8");

console.log(markdownPath);
console.log(jsonPath);
