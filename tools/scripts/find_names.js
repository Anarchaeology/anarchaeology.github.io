// Finds posts by, and posts mentioning, the given names in the saved forum pages (archive and live-forum copies).
// Writes found.json: one entry per matching post with thread, page, date, author, whether it's by or mentions the
// name, a text excerpt, and the Wayback address of the page.
const fs = require('fs'), zlib = require('zlib'), path = require('path');
const [root, outPath, ...names] = process.argv.slice(2);
const nameRx = new RegExp('\\b(' + names.map(n => n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|') + ')\\b', 'i');
const strip = s => s.replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]*>/g, ' ').replace(/&nbsp;/g, ' ').replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&#39;/g, "'").replace(/[ \t]+/g, ' ').replace(/\n\s*\n+/g, '\n').trim();
const months = { Jan: '01', Feb: '02', Mar: '03', Apr: '04', May: '05', Jun: '06', Jul: '07', Aug: '08', Sep: '09', Oct: '10', Nov: '11', Dec: '12', January: '01', February: '02', March: '03', April: '04', June: '06', July: '07', August: '08', September: '09', October: '10', November: '11', December: '12' };
const iso = m => m ? `${m[3]}-${months[m[1]]}-${m[2].padStart(2, '0')}` : '';
const found = [];
for (const [sub, live] of [['', false], ['live', true]]) {
  const dir = path.join(root, sub);
  const meta = new Map(fs.readFileSync(path.join(dir, 'pages.tsv'), 'utf8').split('\n').filter(Boolean).map(l => l.split('\t')).map(f => [f[0] + '_' + f[2], f]));
  for (const file of fs.readdirSync(path.join(dir, 'pages'))) {
    const html = zlib.gunzipSync(fs.readFileSync(path.join(dir, 'pages', file))).toString('utf8');
    if (!nameRx.test(html)) continue;
    const key = file.replace('.html.gz', ''), f = meta.get(key) || [];
    const [id, page] = key.split('_');
    const title = strip((html.match(/<title>([\s\S]*?)<\/title>/) || [])[1] || '').replace(/^Anarchy Online Bulletin Board - /, '').replace(/ - Page \d+$/, '');
    const address = f[4] || ('https://forums-archive.anarchy-online.com/showthread.php?' + (f[1] ? id + '-' + f[1] : id) + (page === '1' ? '' : '/page' + page));
    const wayback = f[3] ? `https://web.archive.org/web/${f[3]}/${address}` : '';
    // Split into posts: archive pages (vBulletin 4) by <li class="postbit...">, live pages by their post anchors.
    const starts = live ? [...html.matchAll(/<a name="post\d+"><\/a>|<!-- post #\d+ -->/g)].map(m => m.index)
                        : [...html.matchAll(/<li class="postbit/g)].map(m => m.index);
    starts.forEach((s, i) => {
      const block = html.slice(s, starts[i + 1] ?? html.length);
      const author = strip(((live ? block.match(/<b>([^<]{1,40})<\/b>|class="bigusername"[^>]*>([^<]+)</) : block.match(/class="username[^"]*"[^>]*>([\s\S]*?)<\/a>/)) || [])
        .slice(1).find(Boolean) || '');
      const bodyMatch = live ? block.match(/<p><font face="verdana, arial, helvetica" size="2" >([\s\S]*?)<\/font><\/p>|<!-- message -->([\s\S]*?)<!-- \/ message -->/)
                             : block.match(/<blockquote class="postcontent restore[^>]*>([\s\S]*?)<\/blockquote>/);
      const body = bodyMatch ? strip(bodyMatch.slice(1).find(Boolean) || '') : '';
      const byName = nameRx.test(author) && nameRx.exec(author)[0].length >= author.trim().length - 2;
      const mentions = nameRx.test(body);
      if (!byName && !mentions) return;
      const d = block.match(/(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]* (\d{1,2})(?:st|nd|rd|th), (\d{4})/);
      found.push({ id: +id, page: +page, title, date: iso(d), author, by: byName, mentions, text: body.slice(0, 1500), wayback, live });
    });
  }
}
found.sort((a, b) => a.date.localeCompare(b.date) || a.id - b.id || a.page - b.page);
fs.writeFileSync(outPath, JSON.stringify(found, null, 1));
const threads = new Set(found.map(f => f.id));
console.log(`${found.length} posts in ${threads.size} threads: ${found.filter(f => f.by).length} written by the names, ${found.filter(f => !f.by && f.mentions).length} by others mentioning them`);
for (const n of names) {
  const r = new RegExp('\\b' + n + '\\b', 'i');
  console.log(`  ${n}: wrote ${found.filter(f => f.by && r.test(f.author)).length}, mentioned in ${found.filter(f => !f.by && r.test(f.text)).length}`);
}
for (const f of found.slice(0, 60)) console.log(`${f.date}  ${f.by ? 'BY ' : '   '} ${f.author.padEnd(18).slice(0, 18)} t${f.id} p${f.page}  ${f.title.slice(0, 55)}${f.live ? '  [live]' : ''}`);
