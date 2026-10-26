// Splits the sites with new pictures into likely-AO ones and the rest, using two signals: an AO-sounding site name,
// and pictures named scrnNNNN (Anarchy Online's own screenshot file names). Writes ao_sites.tsv.
const fs = require('fs'), path = require('path');
const dir = process.argv[2];
const aoName = /(^|[^a-z])(ao|anarchy|omni|rubi|notum|leet|fixer|clan|nano|neutral|atrox|solitus|opifex|nanomage|tek|ofab|grid)/i;
const knownOrgs = /^(ravenslair\.org|4monkeysoup\.com|ergastulum\.com|aodata\.net|beastiary\.|project-starlight\.com|op-for\.net|aidforfriends\.net|hh\.yelloworb\.com|fixergrid\.com|unknownplayer\.com|quillmaster\.|sotoa\.net)/;
const scrn = /\/scrn\d{3,4}[^/]*$/i;
const rows = fs.readFileSync(path.join(dir, 'site_report.tsv'), 'utf8').split('\n').slice(1).filter(Boolean).map(l => l.split('\t'));
const cands = fs.readFileSync(path.join(dir, 'candidates.tsv'), 'utf8').split('\n').filter(Boolean).map(l => l.split('\t'));
const scrnBySite = cands.reduce((m, [site, , url]) => (scrn.test(url) && (m[site] = (m[site] || 0) + 1), m), {});
const out = [], other = [];
for (const [site, total, early, later, note, sample] of rows) {
  if (+early + +later === 0) continue;
  const why = [aoName.test(site.split('/')[0]) && 'AO name', knownOrgs.test(site) && 'AO content seen', scrnBySite[site] && `${scrnBySite[site]} scrn files`].filter(Boolean);
  (why.length && +total <= 3000 ? out : other).push({ site, total: +total, early: +early, later: +later, note, sample, why: why.join(', ') });
}
out.sort((a, b) => b.early - a.early || b.later - a.later);
fs.writeFileSync(path.join(dir, 'ao_sites.tsv'), ['site\tearly\tlater\twhy\tnote\tsample'].concat(out.map(r => [r.site, r.early, r.later, r.why, r.note, r.sample].join('\t'))).join('\n') + '\n');
console.log(`likely AO sites: ${out.length}, pictures first saved by 2003: ${out.reduce((s, r) => s + r.early, 0)}, later: ${out.reduce((s, r) => s + r.later, 0)}`);
console.log(`other sites: ${other.length} (${other.reduce((s, r) => s + r.early, 0)} early pictures)`);
for (const r of out) console.log(`${String(r.early).padStart(5)} ${String(r.later).padStart(5)}  ${r.site.padEnd(36).slice(0, 36)} ${r.why.padEnd(26).slice(0, 26)} ${r.sample.slice(0, 60)}`);
