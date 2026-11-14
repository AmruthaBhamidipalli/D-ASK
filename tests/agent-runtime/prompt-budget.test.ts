/**
 * The plan prompt must fit the local model's context with the FULL product profile (2026-11-14). Found by the packaged acceptance run:
 * with 79 tools offered by the daemon, every tool's argument constraints were written into the prompt (~6,600 tokens > 4,096) and every
 * model-planned request failed with HTTP 400. Run with: npx tsx tests/agent-runtime/prompt-budget.test.ts
 */
import path from "node:path";
import { AgentRuntime } from "../../agent/runtime/runtime";
import { createDefaultRegistry, DEFAULT_GRANTS } from "../../agent/runtime/defaults";
import type { LanguageModel, StructuredRequest, ToolPermission } from "../../agent/runtime/types";

let passed = 0, failed = 0;
const check = (c: boolean, l: string, d?: unknown) => { if (c) { passed++; console.log("  PASS: " + l); } else { failed++; console.error("  FAIL: " + l + (d !== undefined ? ` | ${JSON.stringify(d)}` : "")); } };
const ROOT = path.resolve(__dirname, "..", "..");
const GRANTS = new Set<ToolPermission>([...DEFAULT_GRANTS, "read:desktop", "request:desktop_action"]);
// Every daemon tool offered (the product profile).
const bridge = { available: true, offers: () => true, call: async () => ({ status: "ok", result: { verified: true } }) } as any;

class Measuring implements LanguageModel {
  readonly name = "measuring"; planChars = 0; schemaChars = 0;
  async generateJson(req: StructuredRequest) {
    if (req.purpose === "plan") { this.planChars = req.system.length + req.user.length; this.schemaChars = JSON.stringify(req.schema ?? {}).length; }
    return req.purpose === "plan" ? { intent: "answer", goal: "g", successCriteria: ["x"], steps: [] } : { criteriaSatisfied: true, missing: [], needsMoreSteps: false, reason: "ok" };
  }
}

(async () => {
  const reg = createDefaultRegistry(ROOT, { desktop: bridge });
  check(reg.list().length >= 75, "the full product profile registers every tool", reg.list().length);
  // ~3.2 characters per token for this English/JSON prompt mix: 4,096 tokens leave room for the answer only below ~9,000 characters.
  const BUDGET = 9000;
  for (const q of ["What is the capital of France?", "What is (17+25)*2?", "What's the volume?", "Turn off Wi-Fi", "Open github.com in Chrome",
    "Kill notepad", "Show my startup apps", "Summarise the file C:\\notes\\report.pdf", "Remember that my favourite editor is Neovim."]) {
    const m = new Measuring();
    await new AgentRuntime({ model: m, registry: reg, granted: GRANTS }).run({ taskId: `t${Math.random()}`.replace(".", ""), objective: q });
    const total = m.planChars + m.schemaChars;
    check(m.planChars === 0 || total < BUDGET, `plan prompt + schema fit the local context: "${q}" (${total} chars)`, { prompt: m.planChars, schema: m.schemaChars });
  }
  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exit(1);
})();
