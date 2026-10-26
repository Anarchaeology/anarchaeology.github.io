// Searches the OCR text of the pictures for names, allowing one wrong, missing or extra letter (OCR misreads such
// as "Ieet" for "leet"). Prints each hit with the word read and the text around it.
const fs = require('fs');
const [ocrPath, ...names] = process.argv.slice(2);
const lev = (a, b) => {
  const d = Array.from({ length: a.length + 1 }, (_, i) => [i]);
  for (let j = 1; j <= b.length; j++) d[0][j] = j;
  for (let i = 1; i <= a.length; i++) for (let j = 1; j <= b.length; j++)
    d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
  return d[a.length][b.length];
};
const targets = names.map(n => n.toLowerCase());
let read = 0, errors = 0;
const hits = [];
for (const line of fs.readFileSync(ocrPath, 'utf8').split('\n')) {
  const tab = line.indexOf('\t'); if (tab < 0) continue;
  const file = line.slice(0, tab), text = line.slice(tab + 1);
  read++; if (text.startsWith('ERROR:')) { errors++; continue; }
  for (const m of text.matchAll(/[A-Za-z0-9]+/g)) {
    const w = m[0].toLowerCase();
    for (const t of targets) {
      if (Math.abs(w.length - t.length) > 1) continue;
      const dist = lev(w, t);
      if (dist <= 1) hits.push({ file, word: m[0], target: t, dist, context: text.slice(Math.max(0, m.index - 80), m.index + 80) });
    }
  }
}
console.log(`pictures read: ${read} (${errors} unreadable); hits: ${hits.length} in ${new Set(hits.map(h => h.file)).size} pictures`);
for (const h of hits.sort((a, b) => a.dist - b.dist || a.file.localeCompare(b.file)))
  console.log(`${h.dist === 0 ? 'EXACT' : 'close'}  "${h.word}"  ${h.file.replace(/^.*?[\\/](?=ao_[^\\/]*_photos[\\/])/, '')}\n        ...${h.context.replace(/\s+/g, ' ')}...`);
