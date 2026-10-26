// Matches the forum's linked pictures to the Wayback Machine's saved copies (from CheckWayback.cs's cdx folder),
// and marks the ones already in the collection or already turned down. Writes matched.tsv and extras.tsv.
const fs = require('fs'), path = require('path');
const [checkDir, ...collections] = process.argv.slice(2);
const norm = u => {
  const m = u.match(/^(?:https?:\/\/)?([^/:?#]+)(?::\d+)?(\/[^?#]*)?/i);
  return m ? m[1].toLowerCase().replace(/^www\./, '').replace(/\.$/, '') + (m[2] || '/').toLowerCase() : u.toLowerCase();
};
// Earliest saved copy of each picture address.
const saved = new Map(), byFolder = new Map();
for (const f of fs.readdirSync(path.join(checkDir, 'cdx'))) {
  for (const line of fs.readFileSync(path.join(checkDir, 'cdx', f), 'utf8').split('\n')) {
    const [, ts, original, mime, status, , length] = line.split(' ');
    if (!original || status !== '200') continue;
    const k = norm(original), cur = saved.get(k);
    if (!cur || ts < cur.ts) saved.set(k, { ts, original, length: +length || 0, mime, folder: f });
  }
}
const have = new Set(), turnedDown = new Set();
for (const dir of collections) {
  for (const [file, set] of [['manifest.tsv', have], ['rejected.tsv', turnedDown], ['turned_down.tsv', turnedDown]]) {
    const p = path.join(dir, file);
    if (!fs.existsSync(p)) continue;
    for (const line of fs.readFileSync(p, 'utf8').split('\n').slice(1)) { const u = line.split('\t')[5]; if (u) set.add(norm(u)); }
  }
}
const rows = [], linked = new Set();
for (const line of fs.readFileSync(path.join(checkDir, 'pictures.tsv'), 'utf8').split('\n')) {
  const [key, url, date, kind, threads, prefix] = line.split('\t');
  if (!key) continue;
  linked.add(key);
  const s = saved.get(key);
  const status = !s ? 'not saved' : have.has(key) ? 'in collection' : turnedDown.has(key) ? 'turned down before' : 'new';
  rows.push({ key, url, date, kind, threads, prefix, status, s });
}
fs.writeFileSync(path.join(checkDir, 'matched.tsv'), ['key\turl\tpost_date\tkind\tthreads\tstatus\tfirst_saved\tsaved_url\tbytes']
  .concat(rows.map(r => [r.key, r.url, r.date, r.kind, r.threads, r.status, r.s ? r.s.ts : '', r.s ? r.s.original : '', r.s ? r.s.length : ''].join('\t'))).join('\n') + '\n');
const count = (list, f) => list.reduce((m, r) => (m[f(r)] = (m[f(r)] || 0) + 1, m), {});
console.log('linked pictures by status:', count(rows, r => r.status));
const fresh = rows.filter(r => r.status === 'new');
console.log('new ones, shown in posts vs signatures:', count(fresh, r => r.kind));
console.log('new ones by year first saved:', count(fresh, r => r.s.ts.slice(0, 4)));
console.log('new ones by size:', count(fresh, r => r.s.length < 5000 ? 'a <5KB' : r.s.length < 20000 ? 'b 5-20KB' : r.s.length < 100000 ? 'c 20-100KB' : 'd 100KB+'));
console.log('new ones by post year:', count(fresh, r => r.date.slice(0, 4)));
const hosts = count(fresh, r => r.key.split('/')[0]);
console.log('new ones, top hosts:', Object.entries(hosts).sort((a, b) => b[1] - a[1]).slice(0, 15).map(([h, n]) => `${h} ${n}`).join(', '));
// Other saved pictures (20 KB+) in the same folders, for folders with at most 200 saved pictures (personal folders).
const folderSizes = count([...saved.values()], s => s.folder);
const extras = [...saved.entries()].filter(([k, s]) => !linked.has(k) && !have.has(k) && !turnedDown.has(k) && s.length >= 20000 && folderSizes[s.folder] <= 200);
fs.writeFileSync(path.join(checkDir, 'extras.tsv'), extras.map(([k, s]) => [k, s.ts, s.original, s.length, s.folder].join('\t')).join('\n') + '\n');
console.log(`other saved pictures (20 KB+) in the same personal folders, not linked: ${extras.length} in ${new Set(extras.map(([, s]) => s.folder)).size} folders`);
console.log('  by year first saved:', count(extras.map(([, s]) => s), s => s.ts.slice(0, 4)));
