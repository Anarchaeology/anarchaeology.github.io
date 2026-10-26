// Cleans the not-yet-checked site prefixes: drops general (non-AO) websites and mangled links, turns links to single
// pages on tripod/homestead/etc. subdomains into the subdomain's root, and drops prefixes we have already checked or
// that the collection's own sources already cover. Writes the full list (checked + cleaned remaining) for CheckWayback.cs.
const fs = require('fs');
const [allPath, remainingPath, outPath] = process.argv.slice(2);
const general = /^(nytimes\.com|cdnow\.com|java\.sun\.com|gimp\.org|winehq\.org|wine\.org|libsdl\.org|theonion\.com|georgecarlin\.com|insultcomicdog\.com|nerf\.org|nerf-uk\.org|icrc\.org|pbskids\.org|mathforum\.org|avert\.org|cert\.org|americanpresident\.org|dar\.org|parecon\.org|duhaime\.org|bettyfordcenter\.org|dmaconsumers\.org|mnd\.bbb\.org|bbbenc\.org|usmm\.org|rtnda\.org|mcspotlight\.org|scottishindoorbowls\.org\.uk|nethack\.org|natural-selection\.org|quakenet\.org|smoothwall\.org|alicebot\.org|archive\.org|aol\.com|livejournal\.com|divx\.com|g4tv\.com|eqlive\.station\.sony\.com|earthandbeyond\.ea\.com|swg-rp\.com|forums2\.warcry\.com|heavy\.com|ipetitions\.com|datemeister\.com|oceanbluepools\.com|netaddiction\.com|playserver4\.com|spilpriser\.dk|ebworld\.com|thesmokinggun\.com|cheats4us\.org|annoyances\.org|annoyyourfriends\.com|scene\.org|spybot\.org|nano\.ku\.dk|nano\.umn\.edu|nanotechnology|omni-med\.com|omnimedmd\.com|omni-tech\.com|omnientertainment\.com|pharma-tek\.com|farma-tek\.com|cancel\.org|lostfrog\.org|lorry\.org|waterthread\.org|gathering\.org|mopi\.org|nizkor\.org|legendmud\.org|coremud\.org|backspace\.org|kewl\.phear\.org|jv16\.org|anzwers\.org|hot-dog\.org|artbyfeng\.com|10six\.com|planettribes\.com|bodlovic\.com|mm\.dfilm\.com|fairrosa\.info|animecrossoverrpg\.com|usagiandmamoru\.com|theanimedojo\.com|codysplace\.com|lbvp\.com|zingexclamationpoint\.net|pokealexintheeye\.com|wecometoplay\.com|massivemultiplayer\.org|intelserv\.net|cube7\.com|guild\.com|clanbase\.com|nationalslagassoc\.org|adrastus\.org|amishrakefight\.org|mediaandthefamily\.org|thegoldensun\.org|sheffy\.org|cthulhulives\.org|nickh\.org|homepage\.mac\.com\/?$)/;
const known = /^(anarchy-online\.com|ao-basher\.com|anarchyarcanum\.com|community\.anarchy-online\.com|hackersquest\.com|anarchyonline\.com|omni-elite\.com|projectrevenant\.com|beastiary\.somnaterrae\.org|xyberviri\.org|desert-winds\.org|antiguardians\.org|omnipotence\.de\.vu)$/;
const subdomainSites = /^[^/]+\.(tripod\.com|homestead\.com|t35\.com|freeservers\.com|50megs\.com|0catch\.com|iwarp\.com|clanpages\.com|gamespage\.com)$/;
const checked = new Set(fs.readFileSync(allPath, 'utf8').split('\n').map(l => l.split('\t')[0]).filter(Boolean));
const remaining = fs.readFileSync(remainingPath, 'utf8').split('\n').filter(Boolean).map(l => l.split('\t'));
for (const [p] of remaining) checked.delete(p);
const cleaned = new Map(), dropped = [];
for (const [p, note] of remaining) {
  let prefix = p;
  if (/[,<>\[\]@=]|\.\.|^http\/$|\.coim\/$|\.exe\/$|\.zip\/$|\.doc\/$/.test(prefix)) { dropped.push(p + ' (mangled link)'); continue; }
  const host = prefix.split('/')[0];
  if (subdomainSites.test(host)) prefix = host + '/';   // the subdomain is the person's whole site
  if (general.test(prefix)) { dropped.push(p + ' (general website)'); continue; }
  if (known.test(host)) { dropped.push(p + ' (already covered)'); continue; }
  if (checked.has(prefix) || cleaned.has(prefix)) continue;
  cleaned.set(prefix, note);
}
const all = [...fs.readFileSync(allPath, 'utf8').split('\n').filter(Boolean).filter(l => checked.has(l.split('\t')[0]))]
  .concat([...cleaned].map(([p, n]) => `${p}\t${n}`));
fs.writeFileSync(outPath, all.join('\n') + '\n');
console.log(`remaining ${remaining.length} -> ${cleaned.size} to check; dropped ${dropped.length}`);
console.log(dropped.map(d => '  ' + d).join('\n'));
