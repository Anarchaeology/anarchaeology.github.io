// Reads the saved pages of Storm's phpBB forum (nanoclan.com/phpbb2) and lists the pictures and links in posts,
// with each post's author, date and topic. phpBB2 (subSilver): author in <span class="name">, date in
// "Posted: Mon Mar 10, 2003 4:15 pm", body in <span class="postbody">. Signatures (after "_________________") are
// kept apart; these pages were saved at the time, so signatures are the ones used then. Writes storm_forum.tsv.
const fs = require('fs'), path = require('path');
const [dir] = process.argv.slice(2);
const fetched = new Map(fs.readFileSync(path.join(dir, 'fetched.tsv'), 'utf8').split('\n').filter(Boolean).map(l => l.split('\t')).map(([i, ts, url]) => [i.padStart(3, '0'), { ts, url }]));
const strip = s => s.replace(/<[^>]*>/g, ' ').replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/\s+/g, ' ').trim();
const out = [], topics = new Map();
for (const file of fs.readdirSync(path.join(dir, 'pages')).sort()) {
  const html = fs.readFileSync(path.join(dir, 'pages', file), 'latin1');
  const topic = strip((html.match(/<title>([\s\S]*?)<\/title>/) || [])[1] || '').replace(/^Storm :: (View topic - )?/, '');
  const meta = fetched.get(file.replace('.html', '')) || {};
  const names = [...html.matchAll(/<span class="name"><a name="\d+"><\/a><b>([\s\S]*?)<\/b><\/span>/g)];
  let posts = 0;
  names.forEach((n, i) => {
    const block = html.slice(n.index, names[i + 1] ? names[i + 1].index : html.length);
    const date = ((block.match(/Posted: ([^<&]+)/) || [])[1] || '').trim();
    const bodies = [...block.matchAll(/<span class="postbody">([\s\S]*?)<\/span>\s*<\/td>/g)].map(m => m[1]);
    posts++;
    bodies.forEach(b => {
      const [body, sig = ''] = b.split(/_{10,}/);
      for (const [part, kind] of [[body, ''], [sig, 'sig-']]) {
        for (const m of part.matchAll(/<img src="([^"]+)"/gi)) if (!/images\/smiles|templates\//i.test(m[1])) out.push([topic, strip(n[1]), date, kind + 'img', m[1], meta.ts, meta.url]);
        for (const m of part.matchAll(/<a href="(https?:[^"]+)"/gi)) out.push([topic, strip(n[1]), date, kind + 'link', m[1].replace(/&amp;/g, '&'), meta.ts, meta.url]);
      }
    });
  });
  topics.set(topic, (topics.get(topic) || 0) + posts);
}
fs.writeFileSync(path.join(dir, 'storm_forum.tsv'), out.map(r => r.join('\t')).join('\n') + '\n');
console.log(`${topics.size} topics, ${[...topics.values()].reduce((a, b) => a + b, 0)} posts; ${out.filter(r => /img$/.test(r[3])).length} pictures, ${out.filter(r => /link$/.test(r[3])).length} links`);
for (const r of out.filter(r => /img$/.test(r[3]))) console.log(`${r[3].padEnd(8)} ${r[2].padEnd(26).slice(0, 26)} ${r[1].padEnd(14).slice(0, 14)} "${r[0].slice(0, 30)}"  ${r[4]}`);
console.log('-- links:');
const hosts = out.filter(r => /link$/.test(r[3])).reduce((m, r) => { const h = r[4].replace(/^https?:\/\//, '').split(/[/:?]/)[0]; m[h] = (m[h] || 0) + 1; return m; }, {});
console.log(Object.entries(hosts).sort((a, b) => b[1] - a[1]).map(([h, n]) => `${h} ${n}`).join(', '));
