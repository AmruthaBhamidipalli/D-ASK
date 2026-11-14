// ─────────────────────────────────────────────────────────────────────────────
// OPTIONAL online scholarly discovery (2026-11-15). Two legitimate public APIs that need no account or key:
//   arXiv   https://export.arxiv.org/api/query     (Atom; open-access preprints)
//   Crossref https://api.crossref.org/works        (JSON; DOI metadata of published works)
// Used only when the user asks to find papers ONLINE and the installation enables it (DASK_RESEARCH_ONLINE=1). Nothing else in D-ASK
// depends on it: offline, the request is refused with a clear reason and local research keeps working. Only these two hosts are ever
// contacted, over HTTPS, with the query the user gave. Results are metadata + abstracts, cited by DOI or arXiv URL, never full texts.
// ─────────────────────────────────────────────────────────────────────────────

export interface Paper {
  /** Stable key for BibTeX, e.g. "velickovic2018graph". */
  key: string;
  title: string;
  authors: string[];
  year: number | null;
  venue: string | null;
  doi: string | null;
  url: string;
  abstract: string | null;
  source: "arxiv" | "crossref";
}

export const SCHOLAR_HOSTS = new Set(["export.arxiv.org", "api.crossref.org"]);
export type HttpGet = (url: string, signal: AbortSignal) => Promise<{ status: number; text: string }>;

/** The only network function scholarly discovery may use: HTTPS GET to the two allowlisted hosts, with a time limit. */
export const allowlistedGet: HttpGet = async (url, signal) => {
  const u = new URL(url);
  if (u.protocol !== "https:" || !SCHOLAR_HOSTS.has(u.hostname)) throw new Error(`refused: ${u.hostname} is not an allowed scholarly source`);
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), 20_000);
  const onAbort = () => ctl.abort();
  signal.addEventListener("abort", onAbort, { once: true });
  try {
    const r = await fetch(u, { signal: ctl.signal, headers: { "User-Agent": "D-ASK-research/1.0 (local desktop assistant)", Accept: "application/json, application/atom+xml" }, redirect: "error" });
    return { status: r.status, text: (await r.text()).slice(0, 2_000_000) };
  } finally { clearTimeout(t); signal.removeEventListener("abort", onAbort); }
};

const STOP = new Set(["a", "an", "the", "of", "on", "for", "and", "in", "to", "with", "via", "using", "from", "towards", "toward", "is", "are", "by"]);
export function bibKey(authors: string[], year: number | null, title: string): string {
  const last = (authors[0] ?? "anon").split(/\s+/).pop()!.normalize("NFKD").replace(/[^A-Za-z]/g, "").toLowerCase() || "anon";
  const word = title.normalize("NFKD").toLowerCase().split(/[^a-z0-9]+/).find((w) => w.length > 2 && !STOP.has(w)) ?? "paper";
  return `${last}${year ?? ""}${word}`;
}

const xmlText = (s: string) => s.replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, "$1").replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, "&").replace(/\s+/g, " ").trim();
const tag = (block: string, name: string) => { const m = new RegExp(`<${name}[^>]*>([\\s\\S]*?)</${name}>`, "i").exec(block); return m ? xmlText(m[1]!) : null; };

export function parseArxiv(atom: string): Paper[] {
  const out: Paper[] = [];
  for (const m of atom.matchAll(/<entry>([\s\S]*?)<\/entry>/g)) {
    const e = m[1]!;
    const title = tag(e, "title"); const id = tag(e, "id");
    if (!title || !id) continue;
    const authors = [...e.matchAll(/<author>\s*<name>([\s\S]*?)<\/name>/g)].map((a) => xmlText(a[1]!));
    const published = tag(e, "published");
    const year = published ? Number(published.slice(0, 4)) || null : null;
    const doi = tag(e, "arxiv:doi");
    out.push({ key: bibKey(authors, year, title), title, authors, year, venue: "arXiv", doi, url: id.replace(/^http:/, "https:"), abstract: tag(e, "summary"), source: "arxiv" });
  }
  return out;
}

export function parseCrossref(json: string): Paper[] {
  const data = JSON.parse(json) as { message?: { items?: any[] } };
  return (data.message?.items ?? []).flatMap((it): Paper[] => {
    const title = Array.isArray(it.title) ? it.title[0] : null;
    if (!title || !it.DOI) return [];
    const authors = (it.author ?? []).map((a: any) => [a.given, a.family].filter(Boolean).join(" ")).filter(Boolean);
    const year = it.issued?.["date-parts"]?.[0]?.[0] ?? it.published?.["date-parts"]?.[0]?.[0] ?? null;
    const abstract = typeof it.abstract === "string" ? it.abstract.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim() : null;
    return [{ key: bibKey(authors, year, title), title: xmlText(title), authors, year, venue: Array.isArray(it["container-title"]) ? it["container-title"][0] ?? null : null,
      doi: String(it.DOI), url: `https://doi.org/${it.DOI}`, abstract, source: "crossref" }];
  });
}

export interface ScholarResult { papers: Paper[]; notes: string[] }

/** Searches both sources; one failing source is reported, not fatal. Duplicates (same DOI or same title) are merged. */
export async function searchScholarly(query: string, get: HttpGet, signal: AbortSignal, max = 10): Promise<ScholarResult> {
  const q = query.trim().slice(0, 200);
  const notes: string[] = [];
  const papers: Paper[] = [];
  const tasks: [string, Promise<Paper[]>][] = [
    ["arXiv", get(`https://export.arxiv.org/api/query?search_query=${encodeURIComponent(`all:${q}`)}&start=0&max_results=${max}&sortBy=relevance`, signal)
      .then((r) => { if (r.status !== 200) throw new Error(`HTTP ${r.status}`); return parseArxiv(r.text); })],
    ["Crossref", get(`https://api.crossref.org/works?query.bibliographic=${encodeURIComponent(q)}&rows=${max}&select=DOI,title,author,issued,published,container-title,abstract`, signal)
      .then((r) => { if (r.status !== 200) throw new Error(`HTTP ${r.status}`); return parseCrossref(r.text); })],
  ];
  for (const [name, t] of tasks) {
    try { papers.push(...(await t)); } catch (e) { notes.push(`${name} could not be reached (${e instanceof Error ? e.message : e}).`); }
  }
  const seen = new Set<string>();
  const unique = papers.filter((p) => { const k = (p.doi ?? p.title).toLowerCase().replace(/\W+/g, ""); if (seen.has(k)) return false; seen.add(k); return true; });
  // Keys must be unique in a .bib file.
  const used = new Map<string, number>();
  for (const p of unique) { const n = used.get(p.key) ?? 0; used.set(p.key, n + 1); if (n) p.key = `${p.key}${String.fromCharCode(97 + n)}`; }
  return { papers: unique, notes };
}

export function paperDocument(p: Paper): string {
  return [`# ${p.title}`, "", `Authors: ${p.authors.join(", ") || "unknown"}`, `Year: ${p.year ?? "unknown"}`, `Venue: ${p.venue ?? "unknown"}`, p.doi ? `DOI: ${p.doi}` : "", `Source: ${p.url}`, "",
    "## Abstract", "", p.abstract ?? "(no abstract in the metadata)"].filter((l) => l !== "").join("\n");
}
