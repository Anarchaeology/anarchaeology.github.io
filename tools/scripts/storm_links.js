// Links in posts about the org Storm: posts that mention "Storm" (capitalised, so weather mostly doesn't count) or
// sit in a thread with Storm in its title, plus live-forum (period) signatures that mention Storm.
// Prints the linked sites with the threads and post dates that mention them; writes storm_links.tsv.
const fs = require('fs'), zlib = require('zlib'), path = require('path');
const [root, outPath] = process.argv.slice(2);
const storm = /\bStorm\b(?!\s*(?:troopers?|of|-?wind|front|clouds?))/;
const strip = s => s.replace(/<[^>]*>/g, ' ').replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const general = /(anarchy-online\.com|anarchyonline\.com|funcom\.|vbulletin|aodb\.info|maximumasp|hackersquest|auno\.org|google\.|yahoo\.|microsoft\.|stratics|ign\.com|gamespy)/i;
const hits = [];
for (const [sub, live] of [['', false], ['live', true]]) {
  const dir = path.join(root, sub);
  for (const file of fs.readdirSync(path.join(dir, 'pages'))) {
    const html = zlib.gunzipSync(fs.readFileSync(path.join(dir, 'pages', file))).toString('utf8');
    if (!storm.test(html)) continue;
    const [id, page] = file.replace('.html.gz', '').split('_');
    const title = strip((html.match(/<title>([\s\S]*?)<\/title>/) || [])[1] || '').replace(/^Anarchy Online Bulletin Board - /, '');
    const stormThread = storm.test(title);
    const starts = live ? [...html.matchAll(/<a name="post\d+"><\/a>|<!-- post #\d+ -->/g)].map(m => m.index)
                        : [...html.matchAll(/<li class="postbit/g)].map(m => m.index);
    starts.forEach((s, i) => {
      const block = html.slice(s, starts[i + 1] ?? html.length);
      const bodyMatch = live ? block.match(/<p><font face="verdana, arial, helvetica" size="2" >([\s\S]*?)<\/font><\/p>|<!-- message -->([\s\S]*?)<!-- \/ message -->/)
                             : block.match(/<blockquote class="postcontent restore[^>]*>([\s\S]*?)<\/blockquote>/);
      const parts = [];
      if (bodyMatch) parts.push(['post', bodyMatch.slice(1).find(Boolean) || '']);
      if (live) { const sig = block.match(/__________________([\s\S]*?)(?:<p align="right">|<!-- \/ sig -->|$)/); if (sig) parts.push(['period signature', sig[1]]); }
      const d = (block.match(/(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]* (\d{1,2})(?:st|nd|rd|th), (\d{4})/) || []);
      const date = d[3] ? `${d[3]} ${d[1]} ${d[2]}` : '';
      for (const [kind, part] of parts) {
        if (!(stormThread || storm.test(strip(part)))) continue;
        for (const m of part.matchAll(/href\s*=\s*["']?(https?:\/\/[^"'\s>]+)/gi)) {
          const url = m[1].replace(/&amp;/g, '&');
          if (general.test(url) || /\.(jpe?g|gif|png|bmp)(\?|$)/i.test(url)) continue;
          hits.push({ host: url.replace(/^https?:\/\//i, '').split(/[/:?#]/)[0].toLowerCase().replace(/^www\./, ''), url, id, title, date, kind });
        }
      }
    });
  }
}
const byHost = new Map();
for (const h of hits) { const e = byHost.get(h.host) || []; e.push(h); byHost.set(h.host, e); }
const rows = [...byHost].sort((a, b) => new Set(b[1].map(h => h.id)).size - new Set(a[1].map(h => h.id)).size);
fs.writeFileSync(outPath, hits.map(h => [h.host, h.url, h.id, h.date, h.kind, h.title].join('\t')).join('\n') + '\n');
console.log(`${hits.length} links in Storm-related posts, ${rows.length} sites`);
for (const [host, list] of rows.slice(0, 40)) {
  const threads = [...new Set(list.map(h => h.id))];
  console.log(`${String(threads.length).padStart(3)} threads  ${host.padEnd(32).slice(0, 32)} ${list[0].date.padEnd(12)} ${list[0].kind.padEnd(16)} "${list[0].title.slice(0, 40)}"  ${list[0].url.slice(0, 60)}`);
}
