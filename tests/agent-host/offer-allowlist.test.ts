/**
 * Every tool name the daemon can offer must be accepted by the host's hello allowlist (2026-11-14). Found by the packaged product
 * profile: files.move and office.create_* (added in an earlier round) were missing, so with file organisation switched on the
 * agent host refused to start ("desktop.tools must be up to 128 desktop.* names") and every request failed.
 * The names are read from the daemon's source (every `const string` tool name), so a new daemon tool cannot be forgotten again.
 * Run with: npx tsx tests/agent-host/offer-allowlist.test.ts
 */
import fs from "node:fs";
import path from "node:path";
import { parseRequest, ProtocolError } from "../../agent/host/protocol";

let passed = 0, failed = 0;
const check = (c: boolean, l: string, d?: unknown) => { if (c) { passed++; console.log("  PASS: " + l); } else { failed++; console.error("  FAIL: " + l + (d !== undefined ? ` | ${JSON.stringify(d)}` : "")); } };
const ROOT = path.resolve(__dirname, "..", "..");

function walk(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(path.join(dir, e.name)) : e.name.endsWith(".cs") ? [path.join(dir, e.name)] : []);
}
const names = new Set<string>();
for (const f of walk(path.join(ROOT, "src", "DAsk.Daemon", "Automation"))) {
  for (const line of fs.readFileSync(f, "utf8").split("\n")) {
    if (!/\bconst string\b/.test(line)) continue;
    if (/\bCompute\s*=\s*"compute"/.test(line)) continue;   // PluginService permission names (files.read, memory.read), not tools
    for (const m of line.matchAll(/"([a-z]+\.[a-z_]+)"/g)) if (!/\.(exe|json|dll)$/.test(m[1]!)) names.add(m[1]!);
  }
}
const all = [...names].sort();
console.log(`${all.length} tool names declared by the daemon`);
check(all.length >= 70 && all.includes("files.move") && all.includes("system.power") && all.includes("chrome.click"), "the daemon's tool names are found in its source", all.length);
const hello = (tools: string[]) => JSON.stringify({ v: 1, id: "h1", op: "hello", params: { token: "t", emergencyStopped: false, stopGeneration: 0, desktop: { proto: 2, tools } } });
for (const n of all) {
  let ok = true;
  try { parseRequest(hello([n])); } catch (e) { ok = !(e instanceof ProtocolError); }
  check(ok, `the host accepts the daemon tool ${n}`);
}
let allOk = true;
try { parseRequest(hello(all)); } catch { allOk = false; }
check(allOk, "the host accepts ALL of them at once (the full product profile)", all.length);
let bad = false;
try { parseRequest(hello(["system.exec"])); } catch (e) { bad = e instanceof ProtocolError; }
check(bad, "a name the daemon does not declare is still refused");
console.log(`\n${passed} passed, ${failed} failed`);
if (failed > 0) process.exit(1);
