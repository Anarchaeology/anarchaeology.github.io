// From the harvest's links.tsv files, lists every distinct linked picture (with its earliest post date and kind)
// and the folders to ask the Wayback Machine about. Folders at the root of a big shared host are not listed as
// folders (that would ask about the whole host); those pictures are asked about one by one instead.
const fs = require('fs');
const [outDir, ...linkFiles] = process.argv.slice(2);
const shared = /(^|\.)(geocities\.com|tripod\.com|angelfire\.com|aol\.com|lycos\.com|homestead\.com|fortunecity\.com|xoom\.com|free\.fr|multimania\.com|wanadoo\.fr|chez\.com|imageshack\.us|photobucket\.com)$/i;
const pics = new Map();   // normalized url -> { url, date, kind, threads }
for (const file of linkFiles) {
  for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
    const [thread, page, date, kind, url] = line.split('\t');
    if (!url || !/img$/.test(kind)) continue;
    const m = url.match(/^https?:\/\/([^/:?#]+)(?::\d+)?(\/[^?#]*)?/i);
    if (!m) continue;
    const host = m[1].toLowerCase().replace(/\.$/, ''), path = m[2] || '/';
    const key = host.replace(/^www\./, '') + path.toLowerCase();
    const p = pics.get(key) || { url, host, path, date: date || '9999', kind, threads: new Set() };
    if (date && date < p.date) p.date = date;
    if (kind === 'img') p.kind = 'img';   // a picture shown in a post wins over the same one in a signature
    p.threads.add(thread);
    pics.set(key, p);
  }
}
const prefixes = new Map();
for (const p of pics.values()) {
  const dir = p.path.slice(0, p.path.lastIndexOf('/') + 1);
  const prefix = dir === '/' && shared.test(p.host) ? p.host + p.path : p.host + dir;
  p.prefix = prefix;
  prefixes.set(prefix, (prefixes.get(prefix) || 0) + 1);
}
fs.writeFileSync(`${outDir}/pictures.tsv`, [...pics.entries()].sort().map(([k, p]) =>
  [k, p.url, p.date, p.kind, [...p.threads].join(','), p.prefix].join('\t')).join('\n') + '\n');
fs.writeFileSync(`${outDir}/prefixes.txt`, [...prefixes.keys()].sort().join('\n') + '\n');
console.log(`${pics.size} distinct pictures (${[...pics.values()].filter(p => p.kind === 'img').length} in posts), ${prefixes.size} folders to ask about`);
