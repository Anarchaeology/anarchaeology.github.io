// CrawlSiteImages.cs
// Politely crawls a few live community sites and lists the pictures they show or link to, then checks
// whether each picture still loads. One request at a time, 1.5 s apart; robots.txt "Disallow" rules for
// all agents are respected; each site is limited to MaxPages pages within its own host.
//
//   dotnet run CrawlSiteImages.cs -- SITES.tsv OUTFOLDER [--max-pages N] [--max-depth N]
//
// SITES.tsv: start URL <tab> label, one site per line.
// Writes OUTFOLDER/pages.tsv (site, url, http status, depth) and OUTFOLDER/images.tsv
// (site, page, picture url, how it appeared, http status, content type, bytes, last-modified, still loads).

#:property PublishAot=false

using System.Net;
using System.Text.RegularExpressions;

const string UserAgent = "Mozilla/5.0 (compatible; AOForumArchiveSurvey/1.0; looking for old Anarchy Online screenshots; one request at a time)";
const int DelayMs = 1500;
int maxPages = 300, maxDepth = 4;

if (args.Length < 2) { Console.Error.WriteLine("Usage: dotnet run CrawlSiteImages.cs -- SITES.tsv OUTFOLDER [--max-pages N] [--max-depth N]"); return 2; }
for (int i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--max-pages") maxPages = int.Parse(args[++i]);
    else if (args[i] == "--max-depth") maxDepth = int.Parse(args[++i]);
}
string outDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outDir);
var sites = File.ReadLines(args[0]).Select(l => l.Split('\t')).Where(f => f[0].StartsWith("http")).Select(f => (Start: f[0].Trim(), Label: f.Length > 1 ? f[1].Trim() : f[0])).ToList();

// Pages that are never worth fetching: account, posting, searching and other per-visitor views.
var skip = new Regex(@"(login|logout|register|profile|memberlist|members\b|/user/|privmsg|/pm\b|post(ing)?\.php|/post\b|/post\?|mode=(reply|quote|newtopic|editpost)|reply|search|calendar|viewonline|groupcp|/abuse|report|/rss|feed|print|faq|/latest\?|sendpassword|lostpassword|javascript:|mailto:|\.(zip|exe|rar|pdf|mp3|wav|avi|mpg|mov|wmv|swf|doc|xls)(\?|$))", RegexOptions.IgnoreCase);
var imageExt = new Regex(@"\.(jpe?g|png|gif|bmp)(\?.*)?$", RegexOptions.IgnoreCase);

if (OperatingSystem.IsWindows()) Power.SetThreadExecutionState(Power.Continuous | Power.SystemRequired);
using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 6 })
{ Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

using var pagesOut = new StreamWriter(Path.Combine(outDir, "pages.tsv"), false) { AutoFlush = true };
pagesOut.WriteLine("site\turl\tstatus\tdepth");
var found = new List<(string Site, string Page, string Img, string How)>();
bool first = true;

foreach (var (start, label) in sites)
{
    var startUri = new Uri(start);
    string HostKey(Uri u) => u.Host.ToLowerInvariant().StartsWith("www.") ? u.Host.ToLowerInvariant()[4..] : u.Host.ToLowerInvariant();
    string host = HostKey(startUri);
    var disallow = await RobotsDisallowAsync(startUri);
    var seen = new HashSet<string> { Normalize(startUri) };
    var queue = new Queue<(Uri Url, int Depth)>();
    queue.Enqueue((startUri, 0));
    // A site may live in a subfolder (e.g. /ao/); start pages outside it are still allowed.
    int pages = 0, imgsBefore = found.Count;
    Console.WriteLine($"== {label}  ({start})");
    while (queue.Count > 0 && pages < maxPages)
    {
        var (url, depth) = queue.Dequeue();
        if (disallow.Any(d => url.PathAndQuery.StartsWith(d))) continue;
        if (!first) await Task.Delay(DelayMs);
        first = false;
        pages++;
        string? html = null; int status = 0;
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            status = (int)resp.StatusCode;
            string type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (resp.IsSuccessStatusCode && type.Contains("html"))
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                html = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 3_000_000));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { status = -1; }
        pagesOut.WriteLine($"{label}\t{url}\t{status}\t{depth}");
        if (html is null) continue;

        var baseUri = url;
        var b = Regex.Match(html, @"<base\s+href=[""']([^""']+)", RegexOptions.IgnoreCase);
        if (b.Success && Uri.TryCreate(url, b.Groups[1].Value, out var bu)) baseUri = bu;

        foreach (Match m in Regex.Matches(html, @"<img\b[^>]*?\bsrc\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase))
            if (Resolve(baseUri, m.Groups[1].Value) is { } img) found.Add((label, url.ToString(), img.ToString(), "shown"));

        foreach (Match m in Regex.Matches(html, @"<a\b[^>]*?\bhref\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase))
        {
            if (Resolve(baseUri, m.Groups[1].Value) is not { } link) continue;
            if (imageExt.IsMatch(link.AbsolutePath)) { found.Add((label, url.ToString(), link.ToString(), "linked")); continue; }
            if (depth >= maxDepth || HostKey(link) != host || skip.IsMatch(link.ToString())) continue;
            if (seen.Add(Normalize(link))) queue.Enqueue((link, depth + 1));
        }
    }
    Console.WriteLine($"   {pages} pages, {found.Count - imgsBefore} picture references");
}

// Check each distinct picture once: does it still load, and what is it?
var distinct = found.Select(f => f.Img).Distinct().ToList();
Console.WriteLine($"Checking {distinct.Count} distinct pictures...");
var info = new Dictionary<string, (int Status, string Type, long Bytes, string LastMod)>();
foreach (var img in distinct)
{
    await Task.Delay(500);
    try
    {
        using var resp = await http.GetAsync(img, HttpCompletionOption.ResponseHeadersRead);
        info[img] = ((int)resp.StatusCode, resp.Content.Headers.ContentType?.MediaType ?? "",
                     resp.Content.Headers.ContentLength ?? -1, resp.Content.Headers.LastModified?.ToString("yyyy-MM-dd") ?? "");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { info[img] = (-1, "", -1, ""); }
}

using (var w = new StreamWriter(Path.Combine(outDir, "images.tsv"), false))
{
    w.WriteLine("site\tpage\timage\thow\tstatus\ttype\tbytes\tlast_modified\tloads");
    foreach (var f in found.DistinctBy(f => (f.Site, f.Img)))
    {
        var i = info[f.Img];
        bool loads = i.Status is >= 200 and < 300 && i.Type.StartsWith("image");
        w.WriteLine($"{f.Site}\t{f.Page}\t{f.Img}\t{f.How}\t{i.Status}\t{i.Type}\t{i.Bytes}\t{i.LastMod}\t{(loads ? "yes" : "no")}");
    }
}
Console.WriteLine($"Wrote {Path.Combine(outDir, "images.tsv")}");
return 0;

static Uri? Resolve(Uri baseUri, string raw)
{
    raw = WebUtility.HtmlDecode(raw.Trim());
    if (raw.StartsWith("data:") || raw.StartsWith("javascript:") || raw.StartsWith("#")) return null;
    if (raw.StartsWith("//")) raw = baseUri.Scheme + ":" + raw;
    if (!Uri.TryCreate(baseUri, raw, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
    return new UriBuilder(u) { Fragment = "" }.Uri;
}

// Session ids and sort/highlight parameters make endless copies of the same page; leave them out of the key.
static string Normalize(Uri u) =>
    Regex.Replace(u.GetLeftPart(UriPartial.Query).ToLowerInvariant(), @"([?&])(sid|s|phpsessid|highlight|sort|order|start=0)=[^&]*", "$1").TrimEnd('?', '&').Replace("https://", "http://");

async Task<List<string>> RobotsDisallowAsync(Uri start)
{
    var rules = new List<string>();
    try
    {
        string txt = await http.GetStringAsync(new Uri(start, "/robots.txt"));
        bool forAll = false;
        foreach (var raw in txt.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase)) forAll = line[11..].Trim() == "*";
            else if (forAll && line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase) && line[9..].Trim() is { Length: > 0 } d) rules.Add(d);
        }
    }
    catch { }
    await Task.Delay(DelayMs);
    return rules;
}

static class Power
{
    public const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
}
