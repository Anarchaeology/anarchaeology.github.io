// Websites linked in 2001-2003 forum posts (and period signatures) that none of our earlier lists or the
// collection already know. Writes new_sites.tsv: site, threads linking it, links, first post date, sample address.
const fs = require('fs');
const [outPath, known, ...linkFiles] = process.argv.slice(2);
const site = u => {
  const m = u.match(/^https?:\/\/([^/:?#]+)(?::\d+)?(\/[^?#]*)?/i);
  if (!m) return null;
  let host = m[1].toLowerCase().replace(/\.$/, '').replace(/^www\d*\./, '');
  // Personal space on shared hosts is a site of its own: keep the first folder (or the ~user part).
  const first = ((m[2] || '/').split('/')[1] || '');
  if (/(^|\.)(geocities\.com|tripod\.com|angelfire\.com|aol\.com|homestead\.com|fortunecity\.com|xoom\.com|free\.fr|multimania\.com|chez\.com|netscape\.net|ifrance\.com|wanadoo\.fr|home\.no\.net|members\.shaw\.ca|members\.rogers\.com|members\.cox\.net|home\.comcast\.net|home\.attbi\.com|hjem\.get2net\.dk|home\.planet\.nl|users\.skynet\.be|ezboard\.com)$/.test(host) && first)
    host += '/' + first.toLowerCase();
  return host;
};
const knownSet = new Set(fs.readFileSync(known, 'utf8').split('\n').map(s => s.trim().toLowerCase().replace(/^www\d*\./, '')).filter(Boolean));
const isKnown = s => { for (let h = s; h; h = h.includes('.') ? h.slice(h.indexOf('.') + 1) : '') { if (knownSet.has(h)) return true; if (!h.includes('.')) break; } return knownSet.has(s.split('/')[0]); };
const sites = new Map();
for (const file of linkFiles) {
  for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
    const [thread, , date, kind, url] = line.split('\t');
    if (!url || !/link$/.test(kind)) continue;
    const s = site(url);
    if (!s || isKnown(s)) continue;
    const e = sites.get(s) || { threads: new Set(), links: 0, first: '9999', sample: url };
    e.threads.add(thread); e.links++;
    if (date && date < e.first) e.first = date;
    sites.set(s, e);
  }
}
const rows = [...sites.entries()].sort((a, b) => b[1].threads.size - a[1].threads.size);
fs.writeFileSync(outPath, rows.map(([s, e]) => [s, e.threads.size, e.links, e.first, e.sample].join('\t')).join('\n') + '\n');
console.log(`${rows.length} websites not seen before; ${rows.filter(([, e]) => e.threads.size >= 3).length} linked from 3+ threads`);
