// Wraps 24/32-bit BMP page images as an IMAGE-ONLY PDF (no text layer): node make-scanned-pdf.mjs <out.pdf> <page1.bmp> [page2.bmp ...]
import fs from "node:fs";
import zlib from "node:zlib";

const [out, ...bmps] = process.argv.slice(2);
function gray(bmpPath) {
  const b = fs.readFileSync(bmpPath);
  const off = b.readUInt32LE(10), w = b.readInt32LE(18), hRaw = b.readInt32LE(22), bpp = b.readUInt16LE(28);
  const h = Math.abs(hRaw), bytes = bpp / 8, stride = Math.ceil((w * bytes) / 4) * 4;
  const px = Buffer.alloc(w * h);
  for (let y = 0; y < h; y++) {
    const src = off + (hRaw > 0 ? h - 1 - y : y) * stride;
    for (let x = 0; x < w; x++) {
      const i = src + x * bytes;
      px[y * w + x] = Math.round(0.114 * b[i] + 0.587 * b[i + 1] + 0.299 * b[i + 2]);
    }
  }
  return { w, h, data: zlib.deflateSync(px) };
}
const objs = [];
const add = (s) => { objs.push(s); return objs.length; };
const catalog = add(null), pagesObj = add(null);
const kids = [];
for (const f of bmps) {
  const img = gray(f);
  const pw = 612, ph = Math.round((612 * img.h) / img.w);
  const imgObj = add(Buffer.concat([Buffer.from(`<< /Type /XObject /Subtype /Image /Width ${img.w} /Height ${img.h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length ${img.data.length} >>\nstream\n`), img.data, Buffer.from("\nendstream")]));
  const content = Buffer.from(`q ${pw} 0 0 ${ph} 0 0 cm /Im0 Do Q`);
  const contentObj = add(Buffer.concat([Buffer.from(`<< /Length ${content.length} >>\nstream\n`), content, Buffer.from("\nendstream")]));
  kids.push(add(`<< /Type /Page /Parent ${pagesObj} 0 R /MediaBox [0 0 ${pw} ${ph}] /Resources << /XObject << /Im0 ${imgObj} 0 R >> >> /Contents ${contentObj} 0 R >>`));
}
objs[catalog - 1] = `<< /Type /Catalog /Pages ${pagesObj} 0 R >>`;
objs[pagesObj - 1] = `<< /Type /Pages /Kids [${kids.map((k) => `${k} 0 R`).join(" ")}] /Count ${kids.length} >>`;
const parts = [Buffer.from("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n", "binary")];
const offsets = [];
let pos = parts[0].length;
objs.forEach((o, i) => {
  const body = Buffer.isBuffer(o) ? o : Buffer.from(o);
  const chunk = Buffer.concat([Buffer.from(`${i + 1} 0 obj\n`), body, Buffer.from("\nendobj\n")]);
  offsets.push(pos); parts.push(chunk); pos += chunk.length;
});
const xref = `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n${offsets.map((o) => `${String(o).padStart(10, "0")} 00000 n \n`).join("")}trailer\n<< /Size ${objs.length + 1} /Root ${catalog} 0 R >>\nstartxref\n${pos}\n%%EOF\n`;
parts.push(Buffer.from(xref));
fs.writeFileSync(out, Buffer.concat(parts));
console.log(`wrote ${out} (${bmps.length} image-only pages)`);
