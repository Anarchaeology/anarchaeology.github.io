// Per-site summary of the pictures the Wayback Machine saved on the sites first found in forum posts.
// Leaves out pictures already in the collection or turned down, and pictures under 20 KB (buttons, icons, thumbnails).
// "early" = first saved by the end of 2003; "later" = first saved 2004+ (could still be 2001-2003 uploads; needs a date check).
// Writes site_report.tsv and candidates.tsv (every remaining picture, with its site).
const fs = require('fs'), path = require('path');
const [dir, ...collections] = process.argv.slice(2);
const norm = u => {
  const m = u.match(/^(?:https?:\/\/)?([^/:?#]+)(?::\d+)?(\/[^?#]*)?/i);
  return m ? m[1].toLowerCase().replace(/^www\./, '') + (m[2] || '/').toLowerCase() : u.toLowerCase();
};
const seen = new Set();
for (const c of collections) for (const file of ['manifest.tsv', 'rejected.tsv']) {
  const p = path.join(c, file);
  if (fs.existsSync(p)) for (const l of fs.readFileSync(p, 'utf8').split('\n').slice(1)) { const u = l.split('\t')[5]; if (u) seen.add(norm(u)); }
}
const notes = new Map(fs.readFileSync(path.join(dir, 'prefixes.txt'), 'utf8').split('\n').filter(Boolean).map(l => l.split('\t')));
const fileFor = p => p.replace(/[\/:]/g, '_') + '.txt';
const rows = [], candidates = [];
for (const [prefix, note] of notes) {
  const f = path.join(dir, 'cdx', fileFor(prefix));
  if (!fs.existsSync(f)) continue;
  const pics = new Map();
  for (const line of fs.readFileSync(f, 'utf8').split('\n')) {
    const [, ts, original, , status, , length] = line.split(' ');
    if (!original || status !== '200') continue;
    const k = norm(original);
    if (seen.has(k) || (+length || 0) < 20000) continue;
    const cur = pics.get(k);
    if (!cur || ts < cur.ts) pics.set(k, { ts, original, length: +length });
  }
  const all = [...pics.values()];
  const early = all.filter(p => p.ts < '2004'), later = all.filter(p => p.ts >= '2004');
  const total = fs.readFileSync(f, 'utf8').split('\n').filter(Boolean).length;
  rows.push({ prefix, note, total, early: early.length, later: later.length, sample: early.concat(later).slice(0, 4).map(p => p.original.split('/').pop()).join(' ') });
  for (const p of all) candidates.push([prefix, p.ts, p.original, p.length].join('\t'));
}
rows.sort((a, b) => b.early - a.early || b.later - a.later);
fs.writeFileSync(path.join(dir, 'site_report.tsv'), ['site\tsaved_pictures_all_sizes\tearly_20KB+\tlater_20KB+\tnote\tsample']
  .concat(rows.map(r => [r.prefix, r.total, r.early, r.later, r.note, r.sample].join('\t'))).join('\n') + '\n');
fs.writeFileSync(path.join(dir, 'candidates.tsv'), candidates.join('\n') + '\n');
const big = rows.filter(r => r.total > 3000);
console.log(`sites with any saved pictures: ${rows.filter(r => r.total > 0).length} of ${rows.length}`);
console.log(`sites with 20 KB+ pictures not yet seen: ${rows.filter(r => r.early + r.later > 0).length}; pictures: ${rows.reduce((s, r) => s + r.early, 0)} first saved by 2003, ${rows.reduce((s, r) => s + r.later, 0)} first saved later`);
console.log(`big sites (over 3,000 saved pictures; likely general): ${big.map(r => r.prefix).join(' ')}`);
