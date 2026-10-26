// Turns new_sites.tsv into address prefixes for CheckWayback.cs: the likely AO community sites (linked from 2+
// threads or with an AO-sounding name; general sites and item databases left out). On ISP and shared hosts the
// prefix is the person's own folder (from the sample address), not the whole host. A note after a tab says why.
const fs = require('fs');
const [sitesPath, outPath] = process.argv.slice(2);
const general = /^(cnn\.com|news\.bbc\.co\.uk|dictionary\.com|ebgames\.com|gamespot\.com|play\.com|eve-online\.com|live365\.com|www|jump\.to|webjmp\.com|stripcreator\.com|amazon\.|ebay\.|google\.|yahoo\.|microsoft\.|msn\.|imdb\.com|aodb\.info|v020u46eff\.maximumasp\.com|hackersquest\.org|ao\.hackersquest\.(com|org)|aordb\.cjb\.net|mapquest|cdc\.gov|nasa\.gov|wikipedia|slashdot|penny-arcade|pvponline|somethingawful|bluesnews|gamespy|thinkgeek|dell\.com|apple\.com|ati\.com|nvidia\.com|creative\.com|winamp|mirc|teamspeak|ventrilo|roger-wilco|adobe\.com|sun\.com|java\.com|mozilla|opera\.com|zdnet|cnet|tomshardware|anandtech)/;
const aoName = /(^|[^a-z])(ao|anarchy|omni|clan|neutral|rubi|notum|leet|nano|org|guild|tek)/;
const ispHost = /^(users|home|homepage|homepages|members|member|web|people|hjem|perso|pages|free|sites|user|personal|mysite|webpages)\d*\./;
const prefixes = new Map([['anarchyonline.com/', "Funcom's other address"], ['community.anarchyonline.com/', "Funcom's other address"]]);
for (const line of fs.readFileSync(sitesPath, 'utf8').split('\n')) {
  const [site, threads, , first, sample] = line.split('\t');
  if (!site || general.test(site) || !(+threads >= 2 || aoName.test(site))) continue;
  let prefix = site.includes('/') ? site + '/' : site + '/';
  const m = (sample || '').match(/^https?:\/\/([^/:?#]+)(?::\d+)?\/([^/?#]+)\//i);
  if (!site.includes('/') && (ispHost.test(site) || (m && m[2].startsWith('~')))) {
    if (!m) continue;   // the link was to the host itself; checking the whole ISP host would be too broad
    prefix = site + '/' + m[2] + '/';
  }
  if (!prefixes.has(prefix)) prefixes.set(prefix, `${threads} threads, first ${first}`);
}
fs.writeFileSync(outPath, [...prefixes].map(([p, note]) => `${p}\t${note}`).join('\n') + '\n');
console.log(`${prefixes.size} sites to check`);
