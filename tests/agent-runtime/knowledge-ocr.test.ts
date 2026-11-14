/**
 * Scanned (image-only) PDFs in the knowledge index (2026-11-15): offline OCR with Windows' own PDF renderer and OCR engine (dask-ocr --pdf).
 * Run with: npx tsx tests/agent-runtime/knowledge-ocr.test.ts   (Windows; needs src/hud-native/target/release/dask-ocr.exe)
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { findOcrHelper, OCR_MARK, parseDocument, UnreadableError } from "../../agent/runtime/knowledge/extract";
import { indexCollection } from "../../agent/runtime/knowledge/indexer";
import { retrieve } from "../../agent/runtime/knowledge/retrieve";
import { KnowledgeStore } from "../../agent/runtime/knowledge/store";

let passed = 0, failed = 0;
const check = (c: boolean, l: string, d?: unknown) => { if (c) { passed++; console.log("  PASS: " + l); } else { failed++; console.error("  FAIL: " + l + (d !== undefined ? ` | ${JSON.stringify(d).slice(0, 600)}` : "")); } };

(async () => {
  const helper = findOcrHelper();
  check(helper !== null, "the offline OCR helper is found", helper);
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "dask-ocr-test-"));
  const lib = path.join(tmp, "lib");
  fs.mkdirSync(lib);
  const pdf = path.join(lib, "field-notes-scan.pdf");
  const mk = spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(__dirname, "fixtures", "make-scanned-pdf.ps1"), "-Out", pdf], { encoding: "utf8" });
  check(fs.existsSync(pdf), "an image-only (scanned) PDF fixture is built", mk.stderr);

  // Parsing: both pages recognised, page numbers kept, every block marked as OCR text.
  const doc = parseDocument(pdf, fs.readFileSync(pdf));
  const all = doc.blocks.map((b) => b.text).join(" ");
  check(JSON.stringify(doc.ocrPages) === "[1,2]", "both scanned pages were recognised", doc.ocrPages);
  check(/sediment cores/i.test(all) && /pollen counts doubled/i.test(all), "the recognised text is the page text", all);
  check(doc.blocks.every((b) => b.heading.includes(OCR_MARK)), "every block from a scanned page says it was recognised by OCR", doc.blocks.map((b) => b.heading));
  check(doc.blocks.some((b) => b.page === 2 && /pollen/i.test(b.text)) && doc.blocks.some((b) => b.page === 1 && /sediment/i.test(b.text)), "page numbers are kept");

  // OCR switched off: the scanned PDF is reported unreadable (never guessed).
  process.env.DASK_KNOWLEDGE_OCR = "0";
  let refused = false;
  try { parseDocument(pdf, fs.readFileSync(pdf)); } catch (e) { refused = e instanceof UnreadableError; }
  check(refused, "without OCR a scanned PDF is reported unreadable, as before");
  delete process.env.DASK_KNOWLEDGE_OCR;

  // Indexing + retrieval: a question answered from page 2 of the scan, cited with its page.
  const store = new KnowledgeStore(path.join(tmp, "k.db"));
  store.upsertCollection("scans", "personal", [lib]);
  const r = await indexCollection({ store, embedder: null }, "scans", "personal", [lib], new AbortController().signal);
  check(r.indexed === 1 && r.failed === 0, "the scanned PDF is indexed (keyword search; no embedding model in this test)", r);
  const hit = await retrieve(store, null, "pollen counts after 1950", ["scans"], {});
  const top = hit.passages[0]?.chunk as { page: number | null; text: string; heading: string } | undefined;
  check(!!top && top.page === 2 && /pollen/i.test(top.text), "a question is answered from page 2 of the scan, with the page cited", top);
  check(!!top && top.heading.includes(OCR_MARK), "the citation says the text was recognised by OCR", top?.heading);
  store.close?.();
  fs.rmSync(tmp, { recursive: true, force: true });
  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exit(1);
})();
