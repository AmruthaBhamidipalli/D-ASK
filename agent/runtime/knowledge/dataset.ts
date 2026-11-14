// ─────────────────────────────────────────────────────────────────────────────
// Dataset analysis (2026-11-15), fully offline: a CSV/TSV file -> descriptive statistics, correlations, a least-squares fit, a group
// comparison (Welch's t-test) when the data has an experiment/condition column, SVG charts, and a reproducible report.
// Every number is COMPUTED here (never by the language model); the report records the file's SHA-256, the rows used and the exact
// computations, so it can be reproduced. Measurements are kept apart from interpretation: the report states what was computed only.
// ─────────────────────────────────────────────────────────────────────────────

import { createHash } from "node:crypto";

export interface Table { header: string[]; rows: string[][] }

/** RFC 4180-style CSV (quoted fields, doubled quotes, CRLF) or TSV. */
export function parseDelimited(text: string, delimiter?: string): Table {
  const d = delimiter ?? (text.split("\n", 1)[0]!.includes("\t") ? "\t" : text.split("\n", 1)[0]!.includes(";") && !text.split("\n", 1)[0]!.includes(",") ? ";" : ",");
  const rows: string[][] = [];
  let row: string[] = [], field = "", inQ = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i]!;
    if (inQ) {
      if (c === '"') { if (text[i + 1] === '"') { field += '"'; i++; } else inQ = false; }
      else field += c;
    } else if (c === '"' && field === "") inQ = true;
    else if (c === d) { row.push(field); field = ""; }
    else if (c === "\n" || c === "\r") {
      if (c === "\r" && text[i + 1] === "\n") i++;
      row.push(field); field = "";
      if (row.some((x) => x.trim() !== "")) rows.push(row);
      row = [];
    } else field += c;
  }
  row.push(field);
  if (row.some((x) => x.trim() !== "")) rows.push(row);
  if (rows.length < 2) throw new Error("the file needs a header row and at least one data row");
  const header = rows[0]!.map((h, i) => h.trim() || `column${i + 1}`);
  return { header, rows: rows.slice(1).map((r) => header.map((_, i) => (r[i] ?? "").trim())) };
}

const num = (s: string): number | null => { if (s === "" || /^(na|n\/a|nan|null|-)$/i.test(s)) return null; const v = Number(s.replace(/,(?=\d{3}\b)/g, "")); return Number.isFinite(v) ? v : null; };

export interface NumericSummary { column: string; n: number; missing: number; mean: number; sd: number; min: number; q1: number; median: number; q3: number; max: number }
export interface CategoricalSummary { column: string; n: number; missing: number; distinct: number; top: { value: string; count: number }[] }

function quantile(sorted: number[], q: number): number {
  const pos = (sorted.length - 1) * q, lo = Math.floor(pos), hi = Math.ceil(pos);
  return sorted[lo]! + (sorted[hi]! - sorted[lo]!) * (pos - lo);
}
export function summarise(values: number[]): Omit<NumericSummary, "column" | "missing"> {
  const s = [...values].sort((a, b) => a - b), n = s.length;
  const mean = s.reduce((a, b) => a + b, 0) / n;
  const sd = n > 1 ? Math.sqrt(s.reduce((a, b) => a + (b - mean) ** 2, 0) / (n - 1)) : 0;
  return { n, mean, sd, min: s[0]!, q1: quantile(s, 0.25), median: quantile(s, 0.5), q3: quantile(s, 0.75), max: s[n - 1]! };
}

export function pearson(x: number[], y: number[]): number {
  const n = x.length, mx = x.reduce((a, b) => a + b, 0) / n, my = y.reduce((a, b) => a + b, 0) / n;
  let sxy = 0, sxx = 0, syy = 0;
  for (let i = 0; i < n; i++) { sxy += (x[i]! - mx) * (y[i]! - my); sxx += (x[i]! - mx) ** 2; syy += (y[i]! - my) ** 2; }
  return sxx && syy ? sxy / Math.sqrt(sxx * syy) : 0;
}

export function linearFit(x: number[], y: number[]): { slope: number; intercept: number; r2: number } {
  const n = x.length, mx = x.reduce((a, b) => a + b, 0) / n, my = y.reduce((a, b) => a + b, 0) / n;
  let sxy = 0, sxx = 0;
  for (let i = 0; i < n; i++) { sxy += (x[i]! - mx) * (y[i]! - my); sxx += (x[i]! - mx) ** 2; }
  const slope = sxx ? sxy / sxx : 0;
  return { slope, intercept: my - slope * mx, r2: pearson(x, y) ** 2 };
}

// Regularised incomplete beta (continued fraction, Numerical Recipes) for the Student t distribution.
function lgamma(z: number): number {
  const g = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];
  let x = z, y = z, tmp = x + 5.5; tmp -= (x + 0.5) * Math.log(tmp);
  let ser = 1.000000000190015;
  for (const c of g) ser += c / ++y;
  return -tmp + Math.log((2.5066282746310005 * ser) / x);
}
function betacf(a: number, b: number, x: number): number {
  let qab = a + b, qap = a + 1, qam = a - 1, c = 1, d = 1 - (qab * x) / qap;
  if (Math.abs(d) < 1e-30) d = 1e-30;
  d = 1 / d; let h = d;
  for (let m = 1; m <= 200; m++) {
    const m2 = 2 * m;
    let aa = (m * (b - m) * x) / ((qam + m2) * (a + m2));
    d = 1 + aa * d; if (Math.abs(d) < 1e-30) d = 1e-30; c = 1 + aa / c; if (Math.abs(c) < 1e-30) c = 1e-30; d = 1 / d; h *= d * c;
    aa = (-(a + m) * (qab + m) * x) / ((a + m2) * (qap + m2));
    d = 1 + aa * d; if (Math.abs(d) < 1e-30) d = 1e-30; c = 1 + aa / c; if (Math.abs(c) < 1e-30) c = 1e-30; d = 1 / d;
    const del = d * c; h *= del;
    if (Math.abs(del - 1) < 3e-12) break;
  }
  return h;
}
export function incBeta(a: number, b: number, x: number): number {
  if (x <= 0) return 0; if (x >= 1) return 1;
  const bt = Math.exp(lgamma(a + b) - lgamma(a) - lgamma(b) + a * Math.log(x) + b * Math.log(1 - x));
  return x < (a + 1) / (a + b + 2) ? (bt * betacf(a, b, x)) / a : 1 - (bt * betacf(b, a, 1 - x)) / b;
}
/** Two-sided p-value of Student's t with df degrees of freedom. */
export function tTwoSidedP(t: number, df: number): number { return incBeta(df / 2, 0.5, df / (df + t * t)); }

export function welch(a: number[], b: number[]): { t: number; df: number; p: number; meanA: number; meanB: number; diff: number; cohenD: number } {
  const sa = summarise(a), sb = summarise(b);
  const va = sa.sd ** 2 / sa.n, vb = sb.sd ** 2 / sb.n;
  const t = (sa.mean - sb.mean) / Math.sqrt(va + vb);
  const df = (va + vb) ** 2 / (va ** 2 / (sa.n - 1) + vb ** 2 / (sb.n - 1));
  const pooled = Math.sqrt(((sa.n - 1) * sa.sd ** 2 + (sb.n - 1) * sb.sd ** 2) / (sa.n + sb.n - 2));
  return { t, df, p: tTwoSidedP(t, df), meanA: sa.mean, meanB: sb.mean, diff: sa.mean - sb.mean, cohenD: pooled ? (sa.mean - sb.mean) / pooled : 0 };
}

// ── charts (plain SVG, readable in any browser, no library) ──
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
export function histogramSvg(column: string, values: number[], bins = 12): string {
  const min = Math.min(...values), max = Math.max(...values), w = (max - min) / bins || 1;
  const counts = Array(bins).fill(0) as number[];
  for (const v of values) counts[Math.min(bins - 1, Math.floor((v - min) / w))]!++;
  const top = Math.max(...counts), W = 480, H = 280, P = 40, bw = (W - 2 * P) / bins;
  const bars = counts.map((c, i) => `<rect x="${(P + i * bw).toFixed(1)}" y="${(H - P - ((H - 2 * P) * c) / top).toFixed(1)}" width="${(bw - 2).toFixed(1)}" height="${(((H - 2 * P) * c) / top).toFixed(1)}" fill="#3b82f6"><title>${c}</title></rect>`).join("");
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" font-family="sans-serif" font-size="11"><rect width="100%" height="100%" fill="white"/>` +
    `<text x="${W / 2}" y="18" text-anchor="middle" font-size="13">Distribution of ${esc(column)} (n=${values.length})</text>${bars}` +
    `<line x1="${P}" y1="${H - P}" x2="${W - P}" y2="${H - P}" stroke="#333"/><text x="${P}" y="${H - P + 16}">${min.toPrecision(4)}</text><text x="${W - P}" y="${H - P + 16}" text-anchor="end">${max.toPrecision(4)}</text>` +
    `<text x="${P - 6}" y="${P}" text-anchor="end">${top}</text></svg>`;
}
export function scatterSvg(xName: string, yName: string, x: number[], y: number[], fit: { slope: number; intercept: number; r2: number }): string {
  const W = 480, H = 320, P = 44;
  const [x0, x1, y0, y1] = [Math.min(...x), Math.max(...x), Math.min(...y), Math.max(...y)];
  const sx = (v: number) => P + ((v - x0) / (x1 - x0 || 1)) * (W - 2 * P), sy = (v: number) => H - P - ((v - y0) / (y1 - y0 || 1)) * (H - 2 * P);
  const pts = x.map((v, i) => `<circle cx="${sx(v).toFixed(1)}" cy="${sy(y[i]!).toFixed(1)}" r="3" fill="#ef4444" fill-opacity="0.7"/>`).join("");
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" font-family="sans-serif" font-size="11"><rect width="100%" height="100%" fill="white"/>` +
    `<text x="${W / 2}" y="18" text-anchor="middle" font-size="13">${esc(yName)} vs ${esc(xName)} (least squares, R²=${fit.r2.toFixed(3)})</text>${pts}` +
    `<line x1="${sx(x0).toFixed(1)}" y1="${sy(fit.intercept + fit.slope * x0).toFixed(1)}" x2="${sx(x1).toFixed(1)}" y2="${sy(fit.intercept + fit.slope * x1).toFixed(1)}" stroke="#111" stroke-dasharray="4 3"/>` +
    `<line x1="${P}" y1="${H - P}" x2="${W - P}" y2="${H - P}" stroke="#333"/><line x1="${P}" y1="${P}" x2="${P}" y2="${H - P}" stroke="#333"/>` +
    `<text x="${W / 2}" y="${H - 8}" text-anchor="middle">${esc(xName)}</text><text x="12" y="${H / 2}" transform="rotate(-90 12 ${H / 2})" text-anchor="middle">${esc(yName)}</text></svg>`;
}

export interface Analysis {
  file: string; sha256: string; rows: number; columns: string[];
  numeric: NumericSummary[]; categorical: CategoricalSummary[];
  correlations: { a: string; b: string; r: number; n: number }[];
  fit: { x: string; y: string; slope: number; intercept: number; r2: number; n: number } | null;
  groupTest: { group: string; metric: string; levels: [string, string]; n: [number, number]; t: number; df: number; p: number; diff: number; cohenD: number } | null;
  charts: { name: string; svg: string }[];
}

const GROUP_NAME = /^(group|condition|treatment|arm|variant|cohort|method|model|baseline|class|label)$/i;

export function analyse(file: string, buf: Buffer): Analysis {
  const t = parseDelimited(buf.toString("utf8").replace(/^﻿/, ""));
  const cols = t.header.map((name, i) => ({ name, values: t.rows.map((r) => r[i]!) }));
  const numericCols = cols.filter((c) => { const filled = c.values.filter((v) => v !== ""); return filled.length >= 2 && filled.filter((v) => num(v) !== null).length / filled.length >= 0.9; });
  const numeric: NumericSummary[] = numericCols.map((c) => { const v = c.values.map(num).filter((x): x is number => x !== null); return { column: c.name, missing: c.values.length - v.length, ...summarise(v) }; });
  const categorical: CategoricalSummary[] = cols.filter((c) => !numericCols.includes(c)).map((c) => {
    const counts = new Map<string, number>();
    for (const v of c.values) if (v !== "") counts.set(v, (counts.get(v) ?? 0) + 1);
    return { column: c.name, n: c.values.length - (c.values.filter((v) => v === "").length), missing: c.values.filter((v) => v === "").length, distinct: counts.size,
      top: [...counts].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([value, count]) => ({ value, count })) };
  });
  const pairs = (a: typeof cols[number], b: typeof cols[number]) => { const x: number[] = [], y: number[] = []; for (let i = 0; i < t.rows.length; i++) { const u = num(a.values[i]!), v = num(b.values[i]!); if (u !== null && v !== null) { x.push(u); y.push(v); } } return { x, y }; };
  const correlations: Analysis["correlations"] = [];
  for (let i = 0; i < numericCols.length; i++) for (let j = i + 1; j < numericCols.length; j++) {
    const { x, y } = pairs(numericCols[i]!, numericCols[j]!);
    if (x.length >= 3) correlations.push({ a: numericCols[i]!.name, b: numericCols[j]!.name, r: pearson(x, y), n: x.length });
  }
  correlations.sort((p, q) => Math.abs(q.r) - Math.abs(p.r));
  let fit: Analysis["fit"] = null;
  const charts: Analysis["charts"] = numeric.slice(0, 6).map((s) => ({ name: `histogram-${s.column.replace(/\W+/g, "_")}.svg`, svg: histogramSvg(s.column, numericCols.find((c) => c.name === s.column)!.values.map(num).filter((x): x is number => x !== null)) }));
  if (correlations[0]) {
    const c = correlations[0];
    const { x, y } = pairs(numericCols.find((k) => k.name === c.a)!, numericCols.find((k) => k.name === c.b)!);
    const f = linearFit(x, y);
    fit = { x: c.a, y: c.b, ...f, n: x.length };
    charts.push({ name: "scatter-fit.svg", svg: scatterSvg(c.a, c.b, x, y, f) });
  }
  // Experiment evaluation: a two-level group/condition column and a numeric metric -> Welch's t-test.
  let groupTest: Analysis["groupTest"] = null;
  const g = categorical.find((c) => c.distinct === 2 && GROUP_NAME.test(c.column)) ?? categorical.find((c) => c.distinct === 2);
  if (g && numeric.length) {
    const metric = numeric.find((n) => /score|accuracy|acc|time|error|loss|yield|value|result|metric|f1|rate/i.test(n.column)) ?? numeric[0]!;
    const gi = t.header.indexOf(g.column), mi = t.header.indexOf(metric.column);
    const [la, lb] = [g.top[0]!.value, g.top[1]!.value];
    const A = t.rows.filter((r) => r[gi] === la).map((r) => num(r[mi]!)).filter((x): x is number => x !== null);
    const B = t.rows.filter((r) => r[gi] === lb).map((r) => num(r[mi]!)).filter((x): x is number => x !== null);
    if (A.length >= 2 && B.length >= 2) { const w = welch(A, B); groupTest = { group: g.column, metric: metric.column, levels: [la, lb], n: [A.length, B.length], t: w.t, df: w.df, p: w.p, diff: w.diff, cohenD: w.cohenD }; }
  }
  return { file, sha256: createHash("sha256").update(buf).digest("hex"), rows: t.rows.length, columns: t.header, numeric, categorical, correlations, fit, groupTest, charts };
}

const f = (v: number, d = 3) => (Math.abs(v) >= 1e5 || (Math.abs(v) < 1e-3 && v !== 0) ? v.toExponential(2) : v.toFixed(d));

export function analysisReport(a: Analysis, generatedAt: string): string {
  const L: string[] = [`# Dataset analysis: ${a.file.split(/[\\/]/).pop()}`, "",
    `*Generated:* ${generatedAt} by D-ASK, offline. *File:* \`${a.file}\` (SHA-256 \`${a.sha256}\`). *Rows:* ${a.rows}. *Columns:* ${a.columns.length}.`, "",
    "> Every number below was computed by D-ASK from the file above (not by a language model). Re-running the analysis on a file with the same SHA-256 gives the same numbers; the full results are in `analysis.json`. This report states measurements only; interpretation is left to you.", ""];
  if (a.numeric.length) {
    L.push("## Numeric columns", "", "| Column | n | missing | mean | sd | min | Q1 | median | Q3 | max |", "|---|---|---|---|---|---|---|---|---|---|");
    for (const s of a.numeric) L.push(`| ${s.column} | ${s.n} | ${s.missing} | ${f(s.mean)} | ${f(s.sd)} | ${f(s.min)} | ${f(s.q1)} | ${f(s.median)} | ${f(s.q3)} | ${f(s.max)} |`);
    L.push("");
  }
  if (a.categorical.length) {
    L.push("## Categorical columns", "", "| Column | n | missing | distinct | most frequent |", "|---|---|---|---|---|");
    for (const c of a.categorical) L.push(`| ${c.column} | ${c.n} | ${c.missing} | ${c.distinct} | ${c.top.map((t) => `${t.value} (${t.count})`).join(", ")} |`);
    L.push("");
  }
  if (a.correlations.length) {
    L.push("## Correlations (Pearson r, pairwise complete rows)", "", "| A | B | r | n |", "|---|---|---|---|", ...a.correlations.slice(0, 15).map((c) => `| ${c.a} | ${c.b} | ${f(c.r)} | ${c.n} |`), "");
  }
  if (a.fit) L.push("## Least-squares fit (strongest correlation)", "", `${a.fit.y} = ${f(a.fit.slope, 4)} × ${a.fit.x} + ${f(a.fit.intercept, 4)}; R² = ${f(a.fit.r2)}; n = ${a.fit.n}. Chart: \`scatter-fit.svg\`. Correlation is not causation.`, "");
  if (a.groupTest) {
    const g = a.groupTest;
    L.push("## Group comparison (Welch's two-sample t-test, two-sided)", "",
      `Metric \`${g.metric}\` by \`${g.group}\`: ${g.levels[0]} (n=${g.n[0]}) vs ${g.levels[1]} (n=${g.n[1]}). Difference of means = ${f(g.diff)}; t = ${f(g.t)}; df = ${f(g.df, 1)}; p = ${f(g.p, 4)}; Cohen's d = ${f(g.cohenD)}.`,
      `At the conventional α = 0.05 the difference is ${g.p < 0.05 ? "statistically significant" : "not statistically significant"}. This test assumes independent observations; check that before relying on it.`, "");
  }
  L.push("## Charts", "", ...a.charts.map((c) => `- \`${c.name}\``), "", "## How to reproduce", "",
    "1. Verify the input file's SHA-256 matches the one above.", "2. Ask D-ASK again: `analyse the dataset <same path>` — the computations are deterministic (no sampling, no model).",
    "3. Or recompute from `analysis.json`, which lists every statistic with its inputs (column names, n).", "");
  return L.join("\n");
}
