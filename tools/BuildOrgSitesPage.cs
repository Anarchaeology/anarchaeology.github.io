// BuildOrgSitesPage.cs
// Turns ao_org_sites.tsv (links found in org descriptions on people.anarchy-online.com, each checked for
// whether it still answers) into one self-contained page for browsing the results.
//
//   dotnet run BuildOrgSitesPage.cs -- ao_org_sites.tsv OUTFOLDER
//
// Writes OUTFOLDER/index.html with the rows embedded, so it opens offline with a double-click.
// If OUTFOLDER/hand_checks.tsv exists (columns: link, verdict "up"/"not up"/"hide", current_address, checked,
// org, note), those verdicts override the automatic check for the links they name; a row with an org applies
// to that org only. Rows for the same org and address are shown once.

#:property PublishAot=false

using System.Text;
using System.Text.Json;

if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet run BuildOrgSitesPage.cs -- ao_org_sites.tsv OUTFOLDER"); return 2; }
var lines = File.ReadAllLines(args[0]);
var head = lines[0].Split('\t');
int Col(string name) => Array.IndexOf(head, name);
int cId = Col("org_id"), cOrg = Col("org"), cMembers = Col("members"), cFaction = Col("faction"), cLink = Col("link"),
    cStatus = Col("status"), cHttp = Col("http"), cFinal = Col("final_address"), cTitle = Col("title"),
    cNote = Col("note"), cContent = Col("content_check"), cDead = Col("dead_detail");

string handPath = Path.Combine(args[1], "hand_checks.tsv");
var hand = File.Exists(handPath)
    ? File.ReadLines(handPath).Skip(1).Select(l => l.Split('\t')).Where(f => f.Length >= 2)
        .ToDictionary(f => (Link: f[0], Org: f.Length > 4 ? f[4] : ""),
                      f => (Verdict: f[1], Now: f.Length > 2 ? f[2] : "", On: f.Length > 3 ? f[3] : "", Note: f.Length > 5 ? f[5] : ""))
    : [];

var rows = new List<Dictionary<string, object>>();
var shown = new HashSet<(string, string)>();
foreach (var line in lines.Skip(1))
{
    var f = line.Split('\t');
    string Get(int i) => i >= 0 && i < f.Length ? f[i].Trim() : "";
    string status = Get(cStatus), note = Get(cNote), content = Get(cContent), dead = Get(cDead), title = Get(cTitle);
    // One group per row, from most to least interesting.
    string group =
        content.StartsWith("mentions") && !title.Contains("Codemonkey", StringComparison.OrdinalIgnoreCase) ? "up" :
        note == "looks like the site is still there" || content.StartsWith("mentions") ? "unclear" :
        status == "dead" && dead.StartsWith("server exists") ? "recheck" :
        status == "live" ? "elsewhere" :
        status == "parked" ? "parked" : "gone";
    string link = Get(cLink);
    string url = link.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? link : "http://" + link;
    string handDetail = "";
    if (hand.TryGetValue((link, Get(cOrg)), out var h) || hand.TryGetValue((link, ""), out h))
    {
        if (h.Verdict == "hide") continue;
        bool up = h.Verdict == "up";
        group = up ? "up" : "elsewhere";
        handDetail = up ? $"Checked by hand{(h.On != "" ? " on " + h.On : "")}: still up"
                        : "Checked by hand: " + (h.Note != "" ? h.Note : "not the org's site any more");
        if (up && h.Now != "") url = h.Now;
    }
    if (!shown.Add((Get(cOrg), url.TrimEnd('/').ToLowerInvariant().Replace("https://", "http://")))) continue;
    // Some links are hand-typed and not valid URIs, so take the host by pattern rather than with Uri.
    string host = System.Text.RegularExpressions.Regex.Match(url.ToLowerInvariant(), @"^[a-z]+://(?:www\d*\.)?([^/:?#\s]+)").Groups[1].Value;
    rows.Add(new()
    {
        ["id"] = Get(cId), ["org"] = Get(cOrg), ["m"] = int.TryParse(Get(cMembers), out int m) ? m : 0,
        ["fac"] = Get(cFaction), ["link"] = link.Replace("&amp;", "&"), ["url"] = url.Replace("&amp;", "&"), ["host"] = host,
        ["g"] = group, ["http"] = Get(cHttp), ["final"] = Get(cFinal), ["title"] = System.Net.WebUtility.HtmlDecode(title),
        ["detail"] = handDetail != "" ? handDetail : group switch
        {
            "up" => content == "mentions the org" ? "Page still names the org" : "Page still mentions Anarchy Online",
            "unclear" => content == "nearly empty page" ? "Answers, but the page is nearly empty (may need JavaScript)" : "Answers, but doesn't mention the org or AO",
            "recheck" => $"Server exists but answered {(Get(cHttp) is "000" or "" ? "nothing" : "HTTP " + Get(cHttp))} on 21 Oct (PHP outage day)",
            "elsewhere" => note,
            "parked" => "Domain parked or for sale",
            _ => "Domain no longer exists",
        },
    });
}

string json = JsonSerializer.Serialize(rows).Replace("</", "<\\/");
string checkedOn = File.GetLastWriteTime(args[0]).ToString("d MMMM yyyy");
Directory.CreateDirectory(args[1]);
File.WriteAllText(Path.Combine(args[1], "index.html"),
    Template.Replace("{{DATA}}", json).Replace("{{CHECKED}}", checkedOn).Replace("{{ROWS}}", rows.Count.ToString("N0")),
    new UTF8Encoding(false));
Console.WriteLine($"Wrote {Path.Combine(args[1], "index.html")} ({rows.Count} links)");
return 0;

static partial class Program
{
    const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AO Org Sites</title>
<style>
:root { --bg:#f4f4f1; --fg:#1d2321; --muted:#5f6864; --card:#fff; --line:#d9ddd9; --accent:#2f6f62;
  --up:#2e7d4f; --unclear:#8a6d1d; --recheck:#9a4f12; --elsewhere:#5b5f8f; --parked:#7a5b7a; --gone:#8b8f8d; }
@media (prefers-color-scheme: dark) { :root { --bg:#121615; --fg:#e4e8e5; --muted:#99a39e; --card:#1b2120; --line:#2d3533; --accent:#7cc4b2;
  --up:#6fcf97; --unclear:#e2c169; --recheck:#f0a060; --elsewhere:#a9adf0; --parked:#d7a8d7; --gone:#8f9895; } }
* { box-sizing:border-box }
body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif }
header { padding:20px 16px 8px; max-width:1100px; margin:0 auto }
h1 { margin:0 0 4px; font-size:22px }
.sub { color:var(--muted); margin:0; max-width:70ch }
.controls { position:sticky; top:0; z-index:1; background:var(--bg); border-bottom:1px solid var(--line) }
.bar { max-width:1100px; margin:0 auto; padding:10px 16px; display:flex; flex-wrap:wrap; gap:8px; align-items:center }
.chip { font:inherit; font-size:13px; border:1px solid var(--line); background:var(--card); color:var(--fg); border-radius:999px; padding:5px 11px; cursor:pointer; display:inline-flex; gap:6px; align-items:center }
.chip[aria-pressed="true"] { border-color:var(--accent); box-shadow:inset 0 0 0 1px var(--accent) }
.chip b { font-variant-numeric:tabular-nums }
.dot { width:9px; height:9px; border-radius:50% }
.tools { display:flex; gap:8px; flex-wrap:wrap; width:100% }
input, select { font:inherit; font-size:14px; padding:6px 9px; border:1px solid var(--line); border-radius:6px; background:var(--card); color:var(--fg) }
input[type=search] { flex:1 1 220px; min-width:0 }
.count { color:var(--muted); font-size:13px; align-self:center }
main { max-width:1100px; margin:0 auto; padding:12px 16px 48px; display:grid; gap:8px }
.row { background:var(--card); border:1px solid var(--line); border-left:4px solid var(--c); border-radius:6px; padding:10px 12px; display:grid; grid-template-columns:minmax(0,1fr) auto; gap:4px 12px }
.org { font-weight:600; overflow-wrap:anywhere }
.org a { color:inherit; text-decoration:none } .org a:hover { text-decoration:underline }
.meta { color:var(--muted); font-size:13px; white-space:nowrap; text-align:right }
.site { grid-column:1 / -1; overflow-wrap:anywhere; font-size:14px }
.site a { color:var(--accent) }
.title { color:var(--muted) }
.detail { grid-column:1 / -1; font-size:13px; display:flex; flex-wrap:wrap; gap:4px 12px; align-items:center }
.badge { color:var(--c); font-weight:600 }
.detail a { color:var(--muted) }
.empty { color:var(--muted); padding:24px 0 }
@media (max-width:560px) { .row { grid-template-columns:1fr } .meta { text-align:left } }
</style>
</head>
<body>
<header>
  <h1>Anarchy Online org sites</h1>
  <p class="sub">{{ROWS}} web links found in the Description, Objective and History of every org on Funcom's people site (main Rubi-Ka), each checked on {{CHECKED}}. Org names link to the Funcom org page; "Wayback" opens the site's saved copies.</p>
</header>
<nav class="controls" aria-label="Filters">
  <div class="bar">
    <div id="chips" style="display:flex;flex-wrap:wrap;gap:8px"></div>
    <div class="tools">
      <input id="q" type="search" placeholder="Search org, site or page title" autocomplete="off">
      <select id="fac" aria-label="Faction"><option value="">All factions</option><option>Omni</option><option>Clan</option><option>Neutral</option></select>
      <select id="sort" aria-label="Sort"><option value="m">Most members first</option><option value="org">Org name</option><option value="host">Site address</option></select>
      <span class="count" id="count"></span>
    </div>
  </div>
</nav>
<main id="list" aria-live="polite"></main>
<script>
const DATA = {{DATA}};
const GROUPS = [
  ['up', 'Still up', '--up'],
  ['unclear', 'Answers, unconfirmed', '--unclear'],
  ['recheck', 'Recheck later', '--recheck'],
  ['elsewhere', 'Answers, not the org site', '--elsewhere'],
  ['parked', 'Parked', '--parked'],
  ['gone', 'Domain gone', '--gone'],
];
const state = { g: new Set(['up', 'unclear']), q: '', fac: '', sort: 'm' };
const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
try { const saved = JSON.parse(localStorage.getItem('aoOrgSitesUi') || 'null'); if (saved) { state.g = new Set(saved.g); state.fac = saved.fac || ''; state.sort = saved.sort || 'm'; } } catch (e) {}
const save = () => { try { localStorage.setItem('aoOrgSitesUi', JSON.stringify({ g: [...state.g], fac: state.fac, sort: state.sort })); } catch (e) {} };

const chips = $('chips');
for (const [g, label, color] of GROUPS) {
  const n = DATA.filter(r => r.g === g).length;
  if (!n) continue;
  const b = document.createElement('button');
  b.type = 'button'; b.className = 'chip'; b.dataset.g = g;
  b.innerHTML = `<span class="dot" style="background:var(${color})"></span>${label} <b>${n}</b>`;
  b.onclick = () => { state.g.has(g) ? state.g.delete(g) : state.g.add(g); save(); render(); };
  chips.append(b);
}
$('fac').value = state.fac; $('sort').value = state.sort;
$('fac').onchange = e => { state.fac = e.target.value; save(); render(); };
$('sort').onchange = e => { state.sort = e.target.value; save(); render(); };
let t; $('q').oninput = e => { clearTimeout(t); t = setTimeout(() => { state.q = e.target.value.trim().toLowerCase(); render(); }, 120); };

function render() {
  for (const b of chips.children) b.setAttribute('aria-pressed', String(state.g.has(b.dataset.g)));
  const rows = DATA.filter(r => state.g.has(r.g) && (!state.fac || r.fac === state.fac) &&
    (!state.q || (r.org + ' ' + r.link + ' ' + r.title).toLowerCase().includes(state.q)));
  rows.sort(state.sort === 'm' ? (a, b) => b.m - a.m || a.org.localeCompare(b.org)
    : state.sort === 'org' ? (a, b) => a.org.localeCompare(b.org) : (a, b) => a.host.localeCompare(b.host));
  $('count').textContent = `${rows.length} shown`;
  if (!rows.length) { $('list').innerHTML = '<p class="empty">Nothing matches. Turn on more groups above.</p>'; return; }
  $('list').innerHTML = rows.map(r => {
    const color = GROUPS.find(x => x[0] === r.g)[2];
    const moved = r.final && r.g !== 'gone' && r.g !== 'recheck' && !r.final.toLowerCase().includes(r.host) ? ` · now goes to ${esc(r.final.replace(/^https?:\/\//, '').slice(0, 60))}` : '';
    return `<article class="row" style="--c:var(${color})">
      <div class="org"><a href="https://people.anarchy-online.com/org/stats/d/5/name/${esc(r.id)}" target="_blank" rel="noopener">${esc(r.org)}</a></div>
      <div class="meta">${r.m.toLocaleString('en')} members · ${esc(r.fac)}</div>
      <div class="site"><a href="${esc(r.url)}" target="_blank" rel="noopener">${esc(r.url.replace(/^https?:\/\//, '') !== r.link.replace(/^https?:\/\//, '') && r.g === 'up' ? r.url : r.link)}</a>${r.title ? ` <span class="title">— ${esc(r.title.slice(0, 90))}</span>` : ''}</div>
      <div class="detail"><span class="badge">${esc(GROUPS.find(x => x[0] === r.g)[1])}</span><span>${esc(r.detail)}${moved}</span>
        <a href="https://web.archive.org/web/*/${esc(r.host)}/*" target="_blank" rel="noopener">Wayback</a></div>
    </article>`;
  }).join('');
}
render();
</script>
</body>
</html>
""";
}
