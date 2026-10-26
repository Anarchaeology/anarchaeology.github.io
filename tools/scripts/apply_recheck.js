// Applies the 25 Oct recheck to ao_org_sites.tsv (and its .csv copy): links still dead move from "worth
// rechecking" to gone; links that answered get their new status, address and title, with a note saying what
// actually answered (none turned out to be the org's site).
const fs = require('fs');
const [tsvPath, resultPath, csvPath] = process.argv.slice(2);
const notes = [
  [/auno\.org\/ao\/char\.php/, "auno.org's character list for the org (behind a bot check), not the org's own site"],
  [/home\.austin\.rr\.com/, 'old ISP web space; now redirects to Spectrum webmail'],
  [/britlore\.co\.uk/, 'domain now belongs to an unrelated site (Britannic Lore)'],
  [/unnaturalselection\.net/, 'domain now belongs to an unrelated site (a business book)'],
];
const result = new Map(fs.readFileSync(resultPath, 'utf8').split('\n').filter(Boolean).map(l => l.split('\t')).map(f => [f[0], f]));
const lines = fs.readFileSync(tsvPath, 'utf8').split('\n');
const head = lines[0].split('\t'), col = n => head.indexOf(n);
let gone = 0, answered = 0;
const out = lines.map((l, i) => {
  if (i === 0 || !l) return l;
  const f = l.split('\t');
  if (!/worth rechecking/.test(f[col('dead_detail')])) return l;
  const r = result.get(f[col('link')]);
  if (!r) return l;
  const [, status, code, final, title] = r;
  f[col('http')] = code; f[col('final_address')] = final; f[col('title')] = title;
  if (status === 'dead') {
    f[col('status')] = 'dead';
    f[col('dead_detail')] = `no proper answer on 21 Oct or when rechecked on 25 Oct (${code === '000' ? 'no answer' : 'HTTP ' + code})`;
    gone++;
  } else {
    f[col('status')] = status;
    f[col('dead_detail')] = '';
    f[col('note')] = (notes.find(([rx]) => rx.test(f[col('link')])) || [, 'answered on 25 Oct recheck'])[1];
    answered++;
  }
  return f.join('\t');
});
fs.writeFileSync(tsvPath, out.join('\n'));
const csv = out.filter(Boolean).map(l => l.split('\t').map(v => /[",\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v).join(',')).join('\r\n') + '\r\n';
fs.writeFileSync(csvPath, '﻿' + csv);
console.log(`updated: ${gone} now gone, ${answered} answered (not the org sites)`);
