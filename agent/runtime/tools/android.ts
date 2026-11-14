/**
 * Android devices through the daemon's adb service (2026-11-15). Like the Windows administration tools: a deterministic grammar maps a
 * request to ONE daemon `android.*` call with exactly the arguments the request states (the model never chooses a device, file, address
 * or command). Discovery and device information are reads; connecting over Wi-Fi, file copies and device commands are approved in the
 * HUD by the daemon. A phone that has not authorized this computer is reported with what to do on the phone — never worked around.
 */
import { contentWords } from "../grounding";
import type { ArgumentDomain, GroundingVerdict, ToolSpec } from "../types";
import type { DesktopBridge, DesktopReply } from "./desktop";

export type AndroidTool = "android.list_devices" | "android.device_info" | "android.connect" | "android.push" | "android.pull" | "android.command";
export interface AndroidRequest { tool: AndroidTool; args: Record<string, string | number> }

const P = String.raw`^\s*(?:please\s+|can you\s+|could you\s+|hey d-ask,?\s+)?`;
const END = String.raw`\s*[.!?]?\s*$`;
const R = (s: string) => new RegExp(P + "(?:" + s + ")" + END, "i");
const PHONE = String.raw`(?:my\s+|the\s+)?(?:android\s+)?(?:phone|device|tablet)`;
const WIN_PATH = String.raw`["“]?([A-Za-z]:\\[^"”<>|?*]{1,240}?)["”]?`;
const SD_PATH = String.raw`["“]?(/(?:sdcard|storage/emulated/0)/[^"”;|&<>*?\x60$\\]{1,200}?)["”]?`;

const COMMAND_WORDS: [RegExp, string][] = [
  [/^(?:go\s+(?:to\s+)?(?:the\s+)?home(?:\s+screen)?|press\s+home)$/i, "home"],
  [/^(?:go\s+back|press\s+back)$/i, "back"],
  [/^(?:wake(?:\s+up)?(?:\s+the\s+screen)?|turn\s+(?:the\s+)?screen\s+on)$/i, "wake"],
  [/^(?:lock(?:\s+it)?|turn\s+(?:the\s+)?screen\s+off)$/i, "lock"],
  [/^(?:turn\s+(?:the\s+)?volume\s+up|volume\s+up|raise\s+the\s+volume)$/i, "volume_up"],
  [/^(?:turn\s+(?:the\s+)?volume\s+down|volume\s+down|lower\s+the\s+volume)$/i, "volume_down"],
];
const commandFor = (s: string) => COMMAND_WORDS.find(([re]) => re.test(s.trim()))?.[1] ?? null;

const RULES: [RegExp, (m: RegExpExecArray) => AndroidRequest | null][] = [
  [R(String.raw`(?:list|show)\s+(?:my\s+|the\s+)?(?:connected\s+)?(?:android\s+)?(?:devices|phones)|(?:what|which)\s+(?:android\s+)?(?:devices|phones)\s+are\s+connected|is\s+${PHONE}\s+connected`),
    () => ({ tool: "android.list_devices", args: {} })],
  [R(String.raw`(?:show|get|what(?:'s|\s+is|\s+are))\s+(?:me\s+)?(?:my\s+|the\s+)?(?:android\s+)?(?:phone|device|tablet)(?:'s)?\s+(?:info(?:rmation)?|details|battery(?:\s+level)?|storage|android\s+version|model)`),
    () => ({ tool: "android.device_info", args: {} })],
  [R(String.raw`connect\s+to\s+${PHONE}\s+(?:at|on)\s+(\d{1,3}(?:\.\d{1,3}){3})(?::(\d{4,5}))?(?:\s+over\s+wi-?fi)?`),
    (m) => ({ tool: "android.connect", args: { host: m[1]!, ...(m[2] ? { port: Number(m[2]) } : {}) } })],
  [R(String.raw`(?:copy|send|push|transfer)\s+${WIN_PATH}\s+to\s+${PHONE}(?:\s+(?:in|into)\s+${SD_PATH})?`),
    (m) => ({ tool: "android.push", args: { file: m[1]!.trim(), ...(m[2] ? { toFolder: m[2] } : {}) } })],
  [R(String.raw`(?:copy|pull|get|transfer)\s+${SD_PATH}\s+from\s+${PHONE}\s+(?:to|into)\s+${WIN_PATH}`),
    (m) => ({ tool: "android.pull", args: { remotePath: m[1]!, toFolder: m[2]!.trim() } })],
  [R(String.raw`open\s+(https://[^\s"'\x60;|&]{1,400})\s+on\s+${PHONE}`), (m) => ({ tool: "android.command", args: { action: "open_url", url: m[1]! } })],
  [R(String.raw`(?:on\s+${PHONE},?\s+(.{2,40}?)|(.{2,40}?)\s+on\s+${PHONE}|lock\s+${PHONE})`),
    (m) => { const a = m[0]!.match(/lock\s+(?:my\s+|the\s+)?(?:android\s+)?(?:phone|device|tablet)\s*[.!?]?\s*$/i) && !m[1] && !m[2] ? "lock" : commandFor(m[1] ?? m[2] ?? ""); return a ? { tool: "android.command", args: { action: a } } : null; }],
];

/** The single Android request this objective is, or null. */
export function androidRequest(o: string): AndroidRequest | null {
  if (o.length > 500 || /\b(?:and|then)\b\s+(?:then\s+)?(?:open|copy|send|lock|go|press|turn)\b/i.test(o)) return null;   // one request at a time
  for (const [re, f] of RULES) { const m = re.exec(o); if (m) { const r = f(m); if (r) return r; } }
  return null;
}

const SPECS: { tool: AndroidTool; description: string; change: boolean }[] = [
  { tool: "android.list_devices", description: "List Android devices seen by adb and whether each has authorized this computer.", change: false },
  { tool: "android.device_info", description: "Model, Android version, battery and storage of the connected Android device.", change: false },
  { tool: "android.connect", description: "Connect to an Android device on the local network (wireless debugging). Approved in the HUD; verified in adb's device list.", change: true },
  { tool: "android.push", description: "Copy ONE file from an allowed folder to the phone's shared storage. Approved in the HUD; verified by SHA-256 on the phone.", change: true },
  { tool: "android.pull", description: "Copy ONE file from the phone's shared storage into an allowed folder (never overwrites). Approved in the HUD; verified by SHA-256.", change: true },
  { tool: "android.command", description: "One fixed phone command (home, back, wake, lock, volume, open an https link). Approved in the HUD.", change: true },
];

function evidenceFor(tool: AndroidTool, r: any): string {
  switch (tool) {
    case "android.list_devices":
      return r.count ? `${r.observed}:\n${(r.devices ?? []).map((d: any) => `• ${d.model ?? "device"} (${d.serial}) — ${d.state}${d.wireless ? ", Wi-Fi" : ""}${d.note ? ` — ${d.note}` : ""}`).join("\n")}` : "No Android device is connected.";
    case "android.device_info":
      return `${r.manufacturer} ${r.model} (${r.serial}) — Android ${r.android} (SDK ${r.sdk}); battery ${r.batteryPercent ?? "?"}%${r.charging ? ", charging" : ""}${r.storage ? `; storage: ${r.storage}` : ""}.`;
    case "android.push": return `Copied to ${r.remote}; ${r.observed}.`;
    case "android.pull": return `Saved as ${r.local}; ${r.observed}.`;
    default: return `${r.observed ?? "Done"}.`;
  }
}

const grounded = (reason: string): GroundingVerdict => ({ status: "grounded", reason });
const contradicted = (reason: string): GroundingVerdict => ({ status: "contradicted", reason });
const ungrounded = (reason: string): GroundingVerdict => ({ status: "ungrounded", reason });
const same = (a: unknown, b: unknown) => JSON.stringify(a, Object.keys(a as object).sort()) === JSON.stringify(b, Object.keys(b as object).sort());

function createAndroidTool(bridge: DesktopBridge, spec: (typeof SPECS)[number]): ToolSpec<Record<string, unknown>, any> {
  const offered = () => (bridge.offers ? bridge.offers(spec.tool) : true);
  const req = (o: string) => { if (!offered()) return null; const r = androidRequest(o); return r && r.tool === spec.tool ? r : null; };
  const mine = (o: string) => req(o) !== null;
  const props: Record<string, any> = {
    host: { type: "string", maxLength: 15 }, port: { type: "integer", minimum: 1024, maximum: 65535 }, file: { type: "string", maxLength: 260 },
    toFolder: { type: "string", maxLength: 260 }, remotePath: { type: "string", maxLength: 220 }, action: { type: "string", maxLength: 20 }, url: { type: "string", maxLength: 500 }, serial: { type: "string", maxLength: 64 },
  };
  return {
    name: spec.tool,
    description: spec.description,
    inputSchema: { type: "object", properties: props },
    permissions: spec.change ? ["request:desktop_action"] : ["read:desktop"],
    risk: spec.change ? "medium" : "low",
    requiresApproval: false,
    timeoutMs: 300_000,
    async execute(input, ctx) {
      if (!bridge.available) return { ok: false, error: "Android control is not available (the daemon did not enable it).", evidence: "" };
      let reply: DesktopReply;
      try { reply = await bridge.call(ctx.taskId, spec.tool, { ...input }, ctx.signal, 295_000); }
      catch (e) { return { ok: false, error: e instanceof Error ? e.message : String(e), evidence: "" }; }
      if (reply.status === "cancelled") return { ok: false, error: "The request was cancelled. Nothing was done.", evidence: "" };
      if (reply.status !== "ok") return { ok: false, error: reply.error?.message ?? "Nothing was done.", evidence: "" };
      const r = (reply.result ?? {}) as any;
      if (r.verified !== true) return { ok: false, output: r, error: `${r.observed ?? "The result could not be confirmed"}.`, evidence: "" };
      return { ok: true, output: r, evidence: evidenceFor(spec.tool, r) };
    },
    check: (_i, outcome) => (outcome.output?.verified === true ? { ok: true } : { ok: false, reason: "the result was not confirmed by the device" }),
    requestCheck: {
      argumentDomains: (o): Record<string, ArgumentDomain> => {
        const r = req(o);
        const d: Record<string, ArgumentDomain> = {};
        for (const k of Object.keys(props)) {
          const v = r?.args[k];
          d[k] = v !== undefined ? { kind: "enum", values: [v], optional: false } : { kind: "forbidden", reason: r ? "the request does not give it" : "the request does not ask for this" };
        }
        return d;
      },
      relevant: mine,
      claims: mine,
      facets: (o) => (mine(o) ? [{ id: `${spec.tool}:${JSON.stringify(req(o)!.args)}`, tool: spec.tool, description: spec.description.split(".")[0]!.toLowerCase(), data: req(o)!.args }] : []),
      ground: (input, o) => {
        const r = req(o);
        if (!r) return ungrounded("the request does not ask for this");
        return same(input, r.args) ? grounded("exactly what the request asks") : contradicted(`use exactly ${JSON.stringify(r.args)}`);
      },
      satisfies: (_f, _i, outcome) => outcome.ok && outcome.output?.verified === true,
      stepFor: (f, o) => (f.tool === spec.tool && req(o) ? { ...req(o)!.args } : null),
      vocabulary: (o) => [...contentWords(o)],
    },
  };
}

export const createAndroidTools = (bridge: DesktopBridge): ToolSpec<any, any>[] => SPECS.map((s) => createAndroidTool(bridge, s));
export const ANDROID_TOOLS: readonly AndroidTool[] = SPECS.map((s) => s.tool);
