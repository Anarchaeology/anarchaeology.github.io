// Picks one saved copy per thread page (thread id <= MAXID): prefers a row with a title in its address, then a
// 2025 capture (the full rescue crawl), then the latest. Writes id, slug, page, timestamp sorted by id and page.
const fs = require('fs');
const [rowsPath, outPath, maxId] = process.argv.slice(2);
const best = new Map();
for (const line of fs.readFileSync(rowsPath, 'utf8').split('\n')) {
  const [idText, slug = '', pageText = '1', ts = ''] = line.split('\t');
  const id = +idText, page = +pageText || 1;
  if (!id || id > +maxId || !ts) continue;
  const key = id + '/' + page, cur = best.get(key);
  const score = r => [r.slug ? 1 : 0, r.ts.startsWith('2025') ? 1 : 0, r.ts];
  const row = { id, slug, page, ts };
  if (!cur) { best.set(key, row); continue; }
  const a = score(row), b = score(cur);
  if (a[0] > b[0] || (a[0] === b[0] && (a[1] > b[1] || (a[1] === b[1] && a[2] > b[2])))) best.set(key, row);
}
const rows = [...best.values()].sort((x, y) => x.id - y.id || x.page - y.page);
fs.writeFileSync(outPath, rows.map(r => [r.id, r.slug, r.page, r.ts].join('\t')).join('\n') + '\n');
console.log(`${rows.length} thread pages from ${new Set(rows.map(r => r.id)).size} threads`);
