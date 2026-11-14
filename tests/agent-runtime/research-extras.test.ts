/**
 * Research Mode additions (2026-11-15): optional online discovery (arXiv + Crossref), BibTeX / LaTeX / proposal export, offline dataset
 * analysis. Run with: npx tsx tests/agent-runtime/research-extras.test.ts      (set DASK_TEST_ONLINE=0 to skip the one real online query)
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { KnowledgeService, parseKnowledgeRequest } from "../../agent/runtime/knowledge/service";
import { KnowledgeStore } from "../../agent/runtime/knowledge/store";
import { allowlistedGet, bibKey, parseArxiv, parseCrossref, searchScholarly, type HttpGet } from "../../agent/runtime/knowledge/scholar";
import { analyse, linearFit, pearson, summarise, tTwoSidedP, welch } from "../../agent/runtime/knowledge/dataset";
import { latexEscape } from "../../agent/runtime/knowledge/export";

let passed = 0, failed = 0;
const check = (c: boolean, l: string, d?: unknown) => { if (c) { passed++; console.log("  PASS: " + l); } else { failed++; console.error("  FAIL: " + l + (d !== undefined ? ` | ${JSON.stringify(d).slice(0, 500)}` : "")); } };
const near = (a: number, b: number, tol = 1e-3) => Math.abs(a - b) <= tol;

const ATOM = `<feed><entry><id>http://arxiv.org/abs/1710.10903v3</id><published>2017-10-30T17:41:32Z</published><title>Graph Attention
 Networks</title><summary>We present graph attention networks (GATs), novel neural network architectures that operate on graph-structured data.</summary>
<author><name>Petar Veličković</name></author><author><name>Guillem Cucurull</name></author><arxiv:doi>10.48550/arXiv.1710.10903</arxiv:doi></entry></feed>`;
const CROSSREF = JSON.stringify({ message: { items: [{ DOI: "10.1000/xyz123", title: ["Inductive Representation Learning on Large Graphs"], author: [{ given: "William", family: "Hamilton" }, { given: "Rex", family: "Ying" }],
  issued: { "date-parts": [[2017]] }, "container-title": ["NeurIPS"], abstract: "<jats:p>GraphSAGE learns node embeddings.</jats:p>" }] } });

(async () => {
  console.log("\n[G] Grammar");
  check(JSON.stringify(parseKnowledgeRequest("Find papers on graph attention networks online")) === JSON.stringify({ op: "scholar", query: "graph attention networks" }), "find papers … online");
  check(parseKnowledgeRequest("Search arXiv for diffusion models")?.op === "scholar", "search arXiv for …");
  check(JSON.stringify(parseKnowledgeRequest("Export the research project gnn-2026 as LaTeX")) === JSON.stringify({ op: "export", name: "gnn-2026", format: "latex" }), "export … as LaTeX");
  check(JSON.stringify(parseKnowledgeRequest("Generate the bibliography for gnn-2026")) === JSON.stringify({ op: "export", name: "gnn-2026", format: "bibtex" }), "bibliography for …");
  check(JSON.stringify(parseKnowledgeRequest("Write a research proposal from gnn-2026")) === JSON.stringify({ op: "export", name: "gnn-2026", format: "proposal" }), "proposal from …");
  check(JSON.stringify(parseKnowledgeRequest("Analyse the dataset D:\\data\\trial.csv")) === JSON.stringify({ op: "dataset", path: "D:\\data\\trial.csv" }), "analyse the dataset <path>");
  check(parseKnowledgeRequest("Find papers on graph attention networks")?.op !== "scholar", "without 'online' it stays a LOCAL request (offline-first)");

  console.log("\n[S] Scholarly sources");
  const ax = parseArxiv(ATOM)[0]!;
  check(ax.title === "Graph Attention Networks" && ax.year === 2017 && ax.authors.length === 2 && ax.url === "https://arxiv.org/abs/1710.10903v3" && ax.key === "velickovic2017graph", "arXiv Atom parsed (title, year, authors, URL, key)", ax);
  const cr = parseCrossref(CROSSREF)[0]!;
  check(cr.doi === "10.1000/xyz123" && cr.year === 2017 && cr.venue === "NeurIPS" && cr.abstract === "GraphSAGE learns node embeddings." && cr.key === "hamilton2017inductive", "Crossref JSON parsed", cr);
  check(bibKey(["Ann Lee"], 2020, "The Art of Graphs") === "lee2020art", "BibTeX keys skip stop words");
  let refused = "";
  try { await allowlistedGet("https://example.com/x", new AbortController().signal); } catch (e) { refused = String(e); }
  check(/not an allowed scholarly source/.test(refused), "the network function refuses any host but arXiv and Crossref", refused);
  try { await allowlistedGet("http://export.arxiv.org/api/query", new AbortController().signal); refused = ""; } catch (e) { refused = String(e); }
  check(/not an allowed/.test(refused), "plain HTTP is refused too");

  // A service in a throwaway workspace.
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "dask-research-"));
  const research = path.join(tmp, "research");
  const mk = (scholarGet?: HttpGet) => new KnowledgeService({ store: new KnowledgeStore(path.join(tmp, "k.db")), embedder: null, model: null, workspaceRoot: tmp, extraRoots: [], researchDir: research, ...(scholarGet ? { scholarGet } : {}) });
  const run = async (svc: KnowledgeService, objective: string) => (await svc.run({ taskId: `t${Math.random()}`.replace(".", ""), objective }))!;

  const off = await run(mk(), "Find papers on graph attention networks online");
  check(off.outcome === "refused" && /offline-first/.test(off.response), "offline / not enabled: refused with the reason; local research still offered", off.response);
  const fake: HttpGet = async (url) => ({ status: 200, text: url.includes("arxiv") ? ATOM : CROSSREF });
  const on = await run(mk(fake), "Find papers on graph attention networks online");
  const lib = JSON.parse(fs.readFileSync(path.join(research, "scholar-library.json"), "utf8"));
  check(on.outcome === "completed" && lib.length === 2 && /arxiv\.org\/abs\/1710\.10903/.test(on.response), "papers found, saved to the library, cited by URL", on.response);
  const ask = await run(mk(fake), "Search the scholar collection for graph attention networks");
  check(/arxiv\.org\/abs\/1710\.10903/.test(ask.response), "the discovered abstracts are searchable and cited by their URL (keyword search)", ask.response.slice(0, 300));
  const broken: HttpGet = async () => { throw new Error("getaddrinfo ENOTFOUND"); };
  const fail = await run(mk(broken), "Find papers on graph attention networks online");
  check(fail.outcome === "failed" && /Are you online\? Local research still works offline/.test(fail.response), "online but unreachable: an honest failure", fail.response);

  if (process.env.DASK_TEST_ONLINE !== "0") {
    const real = await searchScholarly("graph attention networks", allowlistedGet, new AbortController().signal, 5);
    console.log(`  (live) ${real.papers.length} paper(s); notes: ${real.notes.join(" ")}; first: ${real.papers[0]?.title} ${real.papers[0]?.url}`);
    check(real.papers.length > 0 && real.papers.every((p) => /^https:\/\/(arxiv\.org|doi\.org)\//.test(p.url)), "LIVE: real arXiv/Crossref search returns papers with DOI/arXiv links", real.notes);
  }

  console.log("\n[E] Exports");
  const projDir = path.join(research, "gnn-test");
  fs.mkdirSync(projDir, { recursive: true });
  fs.writeFileSync(path.join(projDir, "project.json"), JSON.stringify({ id: "gnn-test", topic: "graph neural networks", question: "How do GNNs aggregate neighbours?", type: "literature-review", collections: ["scholar"], latest: false, createdAt: "2026-11-15", versions: [{ version: 1, createdAt: "2026-11-15", report: "report.md", sha256: "x", sources: 2, modelSections: 0 }] }));
  fs.writeFileSync(path.join(projDir, "report.md"), "# Literature review: graph neural networks\n\n## Sources\n\n- **[D1]** Graph Attention Networks — `https://arxiv.org/abs/1710.10903v3`\n- **[D2]** my notes & 50% draft — `C:\\notes\\gnn_draft.md`\n\n## Method\n\nAttention weights neighbours [D1]; sampling scales [D2].\n\n**Evidence**\n\n- [D1] \"masked self-attention\" — p. 2\n");
  fs.writeFileSync(path.join(projDir, "evidence-v1.json"), JSON.stringify({ sources: [{ label: "D1", title: "Graph Attention Networks", path: "https://arxiv.org/abs/1710.10903v3" }, { label: "D2", title: "my notes & 50% draft", path: "C:\\notes\\gnn_draft.md" }],
    sections: [{ title: "Method", synthesis: "Attention weights neighbours [D1].", extracts: [{ doc: "D1", text: "masked self-attention" }] }] }));
  const svc = mk(fake);
  const tex = await run(svc, "Export the research project gnn-test as LaTeX");
  const texFile = fs.readFileSync(path.join(projDir, "report-v1.tex"), "utf8"), bib = fs.readFileSync(path.join(projDir, "references.bib"), "utf8");
  check(tex.outcome === "completed" && /\\cite\{velickovic2017graph\}/.test(texFile) && /@misc\{velickovic2017graph,/.test(bib), "LaTeX report: [D1] -> \\cite{key} of the library paper; entry in references.bib", tex.response);
  check(/50\\% draft/.test(bib + texFile) && !/[^\\]&/.test(texFile.replace(/\\&/g, "")), "special characters are escaped for LaTeX", latexEscape("a & b_c 50%"));
  const prop = await run(svc, "Write a research proposal from gnn-test");
  const propFile = fs.readFileSync(path.join(projDir, "proposal-v1.tex"), "utf8");
  check(prop.outcome === "completed" && /\\section\{Hypotheses\}/.test(propFile) && /To be written by the author/.test(propFile) && /masked self-attention/.test(propFile), "proposal: evidence filled in where it exists, gaps marked for the author (no invented hypotheses)");
  const bibOnly = await run(svc, "Generate the bibliography for gnn-test");
  check(bibOnly.outcome === "completed" && /references\.bib/.test(bibOnly.response), "BibTeX export");

  console.log("\n[D] Dataset analysis");
  const s = summarise([2, 4, 4, 4, 5, 5, 7, 9]);
  check(near(s.mean, 5) && near(s.sd, 2.138) && near(s.median, 4.5) && s.min === 2 && s.max === 9, "descriptive statistics", s);
  check(near(pearson([1, 2, 3, 4], [2, 4, 6, 8]), 1) && near(pearson([1, 2, 3, 4], [8, 6, 4, 2]), -1), "Pearson r");
  const lf = linearFit([1, 2, 3, 4], [3, 5, 7, 9]);
  check(near(lf.slope, 2) && near(lf.intercept, 1) && near(lf.r2, 1), "least-squares fit", lf);
  const w = welch([1, 2, 3, 4, 5], [3, 4, 5, 6, 7]);
  check(near(w.t, -2) && near(w.df, 8) && near(w.p, 0.0805, 5e-4), "Welch's t-test (t=-2, df=8 -> p=0.0805)", w);
  check(near(tTwoSidedP(2.306, 8), 0.05, 5e-4), "t distribution: t=2.306, df=8 -> p=0.05");
  const data = path.join(tmp, "trial.csv");
  const rows = ["participant,group,dose,score"];
  for (let i = 0; i < 20; i++) rows.push(`p${i},${i % 2 ? "treatment" : "control"},${i},${(i % 2 ? 60 : 50) + i * 0.5}`);
  fs.writeFileSync(data, rows.join("\r\n"));
  const a = analyse(data, fs.readFileSync(data));
  check(a.rows === 20 && a.numeric.map((n) => n.column).join() === "dose,score" && a.groupTest?.group === "group" && a.groupTest.metric === "score" && a.groupTest.p < 0.05, "a CSV: numeric/categorical columns found; treatment vs control compared", a.groupTest);
  const ds = await run(svc, `Analyse the dataset ${data}`);
  const outDir = /charts and analysis\.json: (.+)/.exec(ds.response)?.[1]?.trim() ?? "";
  check(ds.outcome === "completed" && /identical results/.test(ds.response) && fs.existsSync(path.join(outDir, "report.md")) && fs.existsSync(path.join(outDir, "scatter-fit.svg")), "dataset analysed offline: report, charts, analysis.json; re-computed identically", ds.response);
  check(/SHA-256/.test(fs.readFileSync(path.join(outDir, "report.md"), "utf8")) && /<svg/.test(fs.readFileSync(path.join(outDir, "scatter-fit.svg"), "utf8")), "the report records the file's SHA-256; the charts are SVG");
  const outside = await run(svc, "Analyse the dataset C:\\Windows\\System32\\drivers\\etc\\hosts.txt");
  check(outside.outcome === "refused" || outside.outcome === "failed", "a file outside the allowed folders is not read", outside.response);

  try { fs.rmSync(tmp, { recursive: true, force: true }); } catch { /* the stores keep the temp database open until exit */ }
  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exit(1);
})();
