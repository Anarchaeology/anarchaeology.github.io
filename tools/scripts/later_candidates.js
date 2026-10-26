// Pictures (20 KB+) on our AO sources whose first saved copy is from 2004 or later and that we have not seen:
// they may still be 2001-2003 uploads (the Cloudeh pattern), which their saved copies' upload dates can show.
// Sources: earlier Wayback listings (raw CDX lines), plus the forum crawl's AO sites and the folders we took pictures
// from. Leaves out obvious later web furniture (svg, icons, WordPress, dated 2004+ paths). Writes later_candidates.tsv.
const fs = require('fs'), path = require('path');
const [outPath, scratch, downloads] = process.argv.slice(2);
const norm = u => { const m = u.match(/^(?:https?:\/\/)?([^/:?#]+)(?::\d+)?(\/[^?#]*)?/i); return m ? m[1].toLowerCase().replace(/^www\./, '') + (m[2] || '/').toLowerCase() : u.toLowerCase(); };
const seen = new Set();
for (const c of fs.readdirSync(downloads).filter(n => /^ao_.*_photos$/.test(n))) for (const f of ['manifest.tsv', 'rejected.tsv', 'turned_down.tsv']) {
  const p = path.join(downloads, c, f);
  if (fs.existsSync(p)) for (const l of fs.readFileSync(p, 'utf8').split('\n').slice(1)) { const u = l.split('\t')[5]; if (u) seen.add(norm(u)); }
}
const junk = /\.(svg|ico|webp)(\?|$)|wp-content|glyphicons|fa-(brands|regular|solid)|\/(200[4-9]|201\d|202\d)[\/_-]|_20(0[4-9]|1\d|2\d)\d{4}|favicon|apple-touch/i;
const first = new Map();   // normalized url -> { ts, url, len, source }
const add = (ts, url, len, source) => {
  if (!url || !ts) return;
  const k = norm(url), cur = first.get(k);
  if (!cur || ts < cur.ts) first.set(k, { ts, url, len: +len || 0, source });
};
// 1. Earlier Wayback listings: raw CDX lines "urlkey ts original mime status digest length".
const cdxDirs = ['basher/wayback/cdx', 'forumshots/cdx', 'jol/wayback/cdx', 'omniorgs/cdx', 'reveries/cdx'].map(d => path.join(scratch, d));
const cdxFiles = cdxDirs.filter(fs.existsSync).flatMap(d => fs.readdirSync(d).map(f => [path.join(d, f), path.basename(path.dirname(d)) === 'wayback' ? path.basename(path.dirname(path.dirname(d))) : path.basename(path.dirname(d))]));
cdxFiles.push([path.join(downloads, 'imageresults.txt'), 'anarchylore']);
for (const [file, source] of cdxFiles) for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
  const [, ts, original, mime, status, , length] = line.split(' ');
  if (status === '200' && /^image\//.test(mime || '')) add(ts, original, length, source);
}
// 2. The forum crawl's AO sites and the folders we took pictures from.
const keepSites = new Set(fs.readFileSync(path.join(downloads, 'ao_forum_harvest/sites/download_sites.txt'), 'utf8').split('\n').filter(Boolean));
for (const l of fs.readFileSync(path.join(downloads, 'ao_forum_harvest/sites/candidates.tsv'), 'utf8').split('\n')) {
  const [site, ts, url, len] = l.split('\t'); if (keepSites.has(site)) add(ts, url, len, 'forum-crawl sites');
}
const coll = fs.readFileSync(path.join(downloads, 'ao_wayback_photos/manifest.tsv'), 'utf8').split('\n').slice(1).map(l => l.split('\t')[5]).filter(Boolean).map(norm);
const keptFolders = new Set(coll.map(k => k.slice(0, k.lastIndexOf('/') + 1)));
for (const l of fs.readFileSync(path.join(downloads, 'ao_forum_harvest/check/extras.tsv'), 'utf8').split('\n')) {
  const [k, ts, url, len] = l.split('\t'); if (k && keptFolders.has(k.slice(0, k.lastIndexOf('/') + 1))) add(ts, url, len, 'forum-crawl folders');
}
const rows = [...first].filter(([k, r]) => r.ts >= '2004' && r.len >= 20000 && !seen.has(k) && !junk.test(r.url)).map(([, r]) => r);
fs.writeFileSync(outPath, rows.map(r => [r.ts, r.url, r.len, r.source].join('\t')).join('\n') + '\n');
const by = rows.reduce((m, r) => (m[r.source] = (m[r.source] || 0) + 1, m), {});
console.log(`${rows.length} later-saved pictures to date-check:`, by);
console.log('by first-saved year:', rows.reduce((m, r) => (m[r.ts.slice(0, 4)] = (m[r.ts.slice(0, 4)] || 0) + 1, m), {}));
