// Flags downloaded forum-linked pictures whose original server says the file was uploaded more than a day after
// the post that linked it: the saved copy may be a later replacement, not the picture the poster showed.
// Writes the flagged ones as a list the contact sheet's "Import list" can read, and as a table.
const fs = require('fs');
const [manifestPath, planPath, outList, outTsv] = process.argv.slice(2);
const norm = u => u.replace(/^https?:\/\//i, '').replace(/:80\//, '/').toLowerCase();
const post = new Map(fs.readFileSync(planPath, 'utf8').split('\n').filter(Boolean).map(l => { const f = l.split('\t'); return [norm(f[3]), f[7]]; }));
const rows = fs.readFileSync(manifestPath, 'utf8').split('\n').slice(1).filter(Boolean).map(l => l.split('\t'));
const days = (a, b) => (Date.parse(a) - Date.parse(b)) / 86400000;
const flagged = rows
  .filter(f => { const p = post.get(norm(f[5])), s = f[8]; return p && s && days(s, p) > 1; })
  .map(f => ({ file: f[2].split('\\').join('/'), url: f[5], posted: post.get(norm(f[5])), uploaded: f[8], date: f[0] }))
  .sort((a, b) => days(b.uploaded, b.posted) - days(a.uploaded, a.posted));
fs.writeFileSync(outList, flagged.map(r => r.url + '\t' + r.file).join('\n') + (flagged.length ? '\n' : ''));
fs.writeFileSync(outTsv, ['file\turl\tposted\tuploaded\tdated_now'].concat(flagged.map(r => [r.file, r.url, r.posted, r.uploaded, r.date].join('\t'))).join('\n') + '\n');
console.log(`${flagged.length} of ${rows.length} were uploaded after the post that linked them:`);
for (const r of flagged) console.log(`  posted ${r.posted}, uploaded ${r.uploaded} (+${Math.round(days(r.uploaded, r.posted))} days)  ${r.file}`);
