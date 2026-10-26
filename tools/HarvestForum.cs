// HarvestForum.cs
// Reads saved thread pages of Funcom's Anarchy Online forum archive (forums-archive.anarchy-online.com, now offline)
// from the Wayback Machine, and collects the links in each post with the post's date.
// One request at a time, 5-7 s apart. A page that fails (5xx/timeouts) is tried twice, 15 s apart, then its latest
// saved copy twice (one copy can be broken while others are fine), then recorded as "error" so one bad page never
// holds up the rest; after three such pages in a row it pauses 30 min, since the Wayback Machine is likely down.
// A 429 ("slow down") is waited out (60 s, doubling up to 15 min) without counting against the page.
// Resumable: pages already in done.tsv are skipped, except "error" ones, which are retried. Keeps the PC awake.
//
// Needs the .NET 10 SDK:
//   dotnet run HarvestForum.cs -- PAGES.tsv OUTFOLDER [--limit N]
//
// PAGES.tsv: thread id <tab> title slug <tab> page number <tab> Wayback timestamp [<tab> exact saved address]
// Writes, in OUTFOLDER:
//   links.tsv  thread id, page, post date (yyyy-MM-dd), kind (img = shown or linked picture, link = other link), url, slug
//   done.tsv   thread id, page, status (ok | noposts | missing | error), posts found, links found
//   pages/<id>_<page>.html.gz   each fetched page, so it can be read again without fetching
//   progress.log
// Only post bodies are read (blockquote.postcontent up to after_content): signatures in the archive are today's,
// not the ones the posters had in 2001-2003, so they are left out.

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

const string UserAgent = "AOForumArchiveSurvey/1.0 (personal research on Anarchy Online history; one request at a time, 5-7 s apart)";
const string Host = "https://forums-archive.anarchy-online.com/showthread.php?";

if (args.Length < 2) { Console.Error.WriteLine("Usage: dotnet run HarvestForum.cs -- PAGES.tsv OUTFOLDER [--limit N]"); return 2; }
string pagesPath = args[0], outDir = args[1];
int limit = args.Length >= 4 && args[2] == "--limit" ? int.Parse(args[3]) : int.MaxValue;
Directory.CreateDirectory(Path.Combine(outDir, "pages"));
string linksPath = Path.Combine(outDir, "links.tsv"), donePath = Path.Combine(outDir, "done.tsv"), logPath = Path.Combine(outDir, "progress.log");

var done = File.Exists(donePath)
    ? File.ReadLines(donePath).Select(l => l.Split('\t')).Where(f => f.Length >= 3 && f[2] != "error").Select(f => f[0] + "/" + f[1]).ToHashSet()
    : new HashSet<string>();
var todo = File.ReadLines(pagesPath).Select(l => l.Split('\t')).Where(f => f.Length >= 4 && !done.Contains(f[0] + "/" + f[2])).ToList();
Log($"start: {todo.Count} pages to read, {done.Count} already done");

Power.SetThreadExecutionState(Power.Continuous | Power.SystemRequired);
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

var dateRx = new Regex(@"<span class=""date"">([A-Z][a-z]{2}) (\d{1,2})(?:st|nd|rd|th), (\d{4})", RegexOptions.Compiled);
// The post body; "postcontent lastedited" (the edit note) and "signature" blockquotes are not matched.
var postRx = new Regex(@"<blockquote class=""postcontent restore", RegexOptions.Compiled);
var attrRx = new Regex(@"\b(src|href)\s*=\s*[""']?([^""'\s>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
var imageExt = new Regex(@"\.(jpe?g|gif|png|bmp)(\?|#|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
var rng = new Random();
int read = 0, totalLinks = 0, errorsInARow = 0, slowDownWait = 60;
foreach (var f in todo.Take(limit))
{
    string id = f[0], slug = f[1], page = f[2], ts = f[3];
    string basePath = slug.Length > 0 ? $"{id}-{slug}" : id;
    // The address forms the archive used: /pageN (vBulletin 4); &page=N and t=ID (older links) as fallbacks.
    var tries = new List<string> { Host + basePath + (page == "1" ? "" : "/page" + page) };
    if (page != "1") tries.Add(Host + basePath + "&page=" + page);
    if (slug.Length == 0) tries.Add(Host + "t=" + id + (page == "1" ? "" : "&page=" + page));
    // An optional fifth column gives the exact saved address (e.g. a page saved from the live forum in 2001-2004).
    if (f.Length >= 5 && f[4].StartsWith("http", StringComparison.OrdinalIgnoreCase)) tries = [f[4]];

    string? html = null;
    bool failed = false;
    foreach (string url in tries)
    {
        (html, bool error) = await Fetch($"https://web.archive.org/web/{ts}id_/{url}");
        // One saved copy can be broken (a lasting 502) while the others are fine: try the latest copy instead.
        if (error) (html, error) = await Fetch($"https://web.archive.org/web/2026id_/{url}");
        failed |= error;
        if (html is not null) break;
    }
    string status;
    int posts = 0, links = 0, pictures = 0;
    // The thread's real title from the page (the address only has a shortened, hyphenated form).
    string title = slug.Replace('-', ' ');
    if (html is not null && Regex.Match(html, @"<title>\s*([^<]*?)\s*</title>") is { Success: true } t)
        title = Regex.Replace(WebUtility.HtmlDecode(t.Groups[1].Value), @" - Page \d+$", "");
    if (html is null) status = failed ? "error" : "missing";
    else
    {
        await using (var gz = new GZipStream(File.Create(Path.Combine(outDir, "pages", $"{id}_{page}.html.gz")), CompressionLevel.SmallestSize))
            await gz.WriteAsync(Encoding.UTF8.GetBytes(html));
        var rows = new List<string>();
        var dates = dateRx.Matches(html).Select(m => (m.Index, Date: ParseDate(m))).ToList();
        foreach (Match post in postRx.Matches(html))
        {
            posts++;
            // Quotes inside posts are divs, not blockquotes, so the first </blockquote> ends the post.
            int end = html.IndexOf("</blockquote>", post.Index, StringComparison.Ordinal);
            string body = html[post.Index..(end >= 0 ? end : html.Length)];
            string date = dates.LastOrDefault(d => d.Index < post.Index).Date ?? "";
            foreach (Match a in attrRx.Matches(body))
            {
                string url = WebUtility.HtmlDecode(a.Groups[2].Value).Trim();
                // The forum's own pages and images (smilies, buttons) are not of interest.
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.Contains("forums.anarchy-online.com", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("forums-archive.anarchy-online.com", StringComparison.OrdinalIgnoreCase)) continue;
                string kind = a.Groups[1].Value.Equals("src", StringComparison.OrdinalIgnoreCase) || imageExt.IsMatch(url) ? "img" : "link";
                rows.Add(string.Join('\t', id, page, date, kind, url, slug));
            }
        }
        rows = rows.Distinct().ToList();
        links = rows.Count;
        pictures = rows.Count(r => r.Split('\t')[3] == "img");
        totalLinks += links;
        File.AppendAllLines(linksPath, rows);
        status = posts > 0 ? "ok" : "noposts";
    }
    File.AppendAllText(donePath, string.Join('\t', id, page, status, posts, links) + "\n");
    read++;
    Log($"{read}/{Math.Min(todo.Count, limit)}  thread {id} \"{title}\" page {page}: {status}, {posts} posts, {links} links ({pictures} pictures)");
    errorsInARow = status == "error" ? errorsInARow + 1 : 0;
    if (errorsInARow >= 3)
    {
        Log("three pages in a row failed; the Wayback Machine is probably down, pausing 30 min");
        await Task.Delay(TimeSpan.FromMinutes(30));
        errorsInARow = 0;
    }
    await Task.Delay(rng.Next(5000, 7001));
}
Power.SetThreadExecutionState(Power.Continuous);
Log($"finished this run: {read} pages read, {totalLinks} links");
return 0;

// Returns the page; or no page and error = false if the Wayback Machine has no copy (404); or no page and
// error = true after two failed tries, 15 s apart. A 429 is waited out and doesn't use up a try.
async Task<(string? Html, bool Error)> Fetch(string url)
{
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound) return (null, false);
            if (resp.IsSuccessStatusCode) { slowDownWait = 60; return (await resp.Content.ReadAsStringAsync(), false); }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Log($"HTTP 429 (asked to slow down); waiting {slowDownWait} s");
                await Task.Delay(slowDownWait * 1000);
                slowDownWait = Math.Min(slowDownWait * 2, 900);
                attempt--;
                continue;
            }
            Log($"HTTP {(int)resp.StatusCode} for {url} (try {attempt})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log($"{ex.GetType().Name} for {url} (try {attempt})");
        }
        if (attempt == 2) return (null, true);
        await Task.Delay(15_000);
    }
}

static string? ParseDate(Match m) =>
    DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}", "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? d.ToString("yyyy-MM-dd") : null;

void Log(string message)
{
    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
    Console.WriteLine(line);
    File.AppendAllText(logPath, line + "\n");
}

static class Power
{
    public const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
}
