// CrawlJolForum.cs
// Politely mirrors one forum from archives.jeuxonline.info (the JOL forum archive) to disk:
// every index page, then every thread page. One request at a time, ~2.5-3.5 s apart, with
// backoff on 429/5xx. Files already on disk are skipped, so the run can be stopped and resumed.
//
// Needs the .NET 10 SDK:
//   dotnet run CrawlJolForum.cs -- --out FOLDER [--forum 63] [--limit N]
//
// Writes FOLDER/index/forum-<id>[-<page>].html, FOLDER/threads/<thread id>.html and
// FOLDER/threads.tsv (id, title, author, replies, last post date, index page).

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

const string BaseUrl = "https://archives.jeuxonline.info";
const string UserAgent =
    "AOForumArchiveSurvey/1.0 (personal research on Anarchy Online history; " +
    "one request at a time, 2-3 s apart)";
const int MinDelayMs = 2000, MaxDelayMs = 3000;
const int MaxAttempts = 5;
const int MaxConsecutiveFailures = 3;

string? outDir = null;
int forum = 63;
int limit = int.MaxValue;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (a == "--forum" && i + 1 < args.Length && int.TryParse(args[i + 1], out forum)) i++;
    else if (a == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out limit) && limit > 0) i++;
    else { Console.Error.WriteLine($"Bad or incomplete option: {a}\nUsage: dotnet run CrawlJolForum.cs -- --out FOLDER [--forum 63] [--limit N]"); return 2; }
}
if (outDir is null) { Console.Error.WriteLine("--out FOLDER is required"); return 2; }

string indexDir = Path.Combine(outDir, "index"), threadDir = Path.Combine(outDir, "threads");
Directory.CreateDirectory(indexDir);
Directory.CreateDirectory(threadDir);
foreach (string stale in Directory.EnumerateFiles(outDir, "*.part", SearchOption.AllDirectories)) File.Delete(stale);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nCtrl+C - stopping..."); };

using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
{ Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

int requests = 0, fetched = 0, threadsFetched = 0, consecutiveFailures = 0;
var failures = new List<(string Url, string Reason)>();
string? stopReason = null;

try
{
    // ---- Index pages ----
    string first = await GetCachedAsync($"/forum-{forum}.html", Path.Combine(indexDir, $"forum-{forum}.html"));
    int pages = Regex.Matches(first, $@"/forum-{forum}-(\d+)\.html").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(1).Max();
    Console.WriteLine($"Forum {forum}: {pages} index pages");
    for (int p = 2; p <= pages; p++)
        await GetCachedAsync($"/forum-{forum}-{p}.html", Path.Combine(indexDir, $"forum-{forum}-{p}.html"));

    // ---- Thread list ----
    var row = new Regex(
        @"<tr class=""fil"">\s*<td><a href=""/fils/(\d+)\.html"">(.*?)</a></td>\s*<td>(.*?)</td>\s*" +
        @"<td class=""nbfils"">(\d+)</td>\s*<td class=""nbfils"">(.*?)</td>",
        RegexOptions.Singleline);
    var threads = new List<string[]>();
    var seen = new HashSet<string>();
    for (int p = 1; p <= pages; p++)
    {
        string file = Path.Combine(indexDir, p == 1 ? $"forum-{forum}.html" : $"forum-{forum}-{p}.html");
        if (!File.Exists(file)) continue;
        foreach (Match m in row.Matches(File.ReadAllText(file, Encoding.UTF8)))
            if (seen.Add(m.Groups[1].Value))
                threads.Add([m.Groups[1].Value, Clean(m.Groups[2].Value), Clean(m.Groups[3].Value),
                             m.Groups[4].Value, Clean(m.Groups[5].Value), p.ToString()]);
    }
    File.WriteAllLines(Path.Combine(outDir, "threads.tsv"),
        threads.Select(t => string.Join('\t', t)).Prepend("id\ttitle\tauthor\treplies\tlast_post\tindex_page"));
    int have = threads.Count(t => File.Exists(Path.Combine(threadDir, $"{t[0]}.html")));
    Console.WriteLine($"{threads.Count} threads listed, {have} already on disk, ~{TimeSpan.FromSeconds((threads.Count - have) * 2.6):h\\:mm} to fetch the rest");

    // ---- Threads (plus any extra pages a long thread links to) ----
    int done = 0;
    foreach (var t in threads)
    {
        if (threadsFetched >= limit) { stopReason = $"--limit {limit} reached"; break; }
        string id = t[0];
        string threadFile = Path.Combine(threadDir, $"{id}.html");
        if (!File.Exists(threadFile)) threadsFetched++;
        string html = await GetCachedAsync($"/fils/{id}.html", threadFile);
        foreach (string extra in Regex.Matches(html, $@"/fils/{id}-(\d+)\.html").Select(m => m.Groups[1].Value).Distinct())
            await GetCachedAsync($"/fils/{id}-{extra}.html", Path.Combine(threadDir, $"{id}-{extra}.html"));
        if (++done % 50 == 0) Console.WriteLine($"  {done}/{threads.Count} threads ({requests} requests this run)");
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested) { stopReason = "interrupted with Ctrl+C"; }
catch (StopException ex) { stopReason = ex.Message; }

Console.WriteLine();
Console.WriteLine($"Requests this run: {requests}   pages saved: {fetched}   failures: {failures.Count}");
if (stopReason is not null) Console.WriteLine($"Stopped early: {stopReason}. Run again to resume.");
foreach (var (url, reason) in failures) Console.WriteLine($"  FAILED {url}  {reason}");
return failures.Count > 0 || stopReason is not null ? 1 : 0;

// Returns the page from disk if present, otherwise downloads it politely and saves it.
// A failed page yields "" so the crawl can carry on; it is retried on the next run.
async Task<string> GetCachedAsync(string path, string file)
{
    if (File.Exists(file)) return await File.ReadAllTextAsync(file, Encoding.UTF8, cts.Token);
    if (requests > 0) await Task.Delay(Random.Shared.Next(MinDelayMs, MaxDelayMs + 1), cts.Token);
    requests++;

    string url = BaseUrl + path;
    for (int attempt = 1; ; attempt++)
    {
        string problem;
        TimeSpan? retryAfter = null;
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, cts.Token);
            int code = (int)resp.StatusCode;
            if (code == 200)
            {
                byte[] body = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                await File.WriteAllBytesAsync(file + ".part", body, cts.Token);
                File.Move(file + ".part", file, overwrite: true);
                fetched++;
                consecutiveFailures = 0;
                return Encoding.UTF8.GetString(body);
            }
            if (code != 429 && code < 500) return Fail($"HTTP {code}");
            problem = $"HTTP {code}";
            retryAfter = resp.Headers.RetryAfter switch
            {
                { Delta: TimeSpan d } => d,
                { Date: DateTimeOffset when } => when - DateTimeOffset.UtcNow,
                _ => null,
            };
        }
        catch (Exception ex) when (!cts.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
        {
            problem = ex is TaskCanceledException ? "request timed out" : ex.Message;
        }

        if (attempt == MaxAttempts) return Fail($"{problem} - gave up after {MaxAttempts} attempts");
        TimeSpan wait = retryAfter is { } ra && ra > TimeSpan.Zero
            ? (ra < TimeSpan.FromHours(1) ? ra : TimeSpan.FromHours(1))
            : TimeSpan.FromSeconds(60 << (attempt - 1));
        Console.WriteLine($"    {path}: {problem}; waiting {wait.TotalSeconds:0} s before attempt {attempt + 1}");
        await Task.Delay(wait, cts.Token);
    }

    string Fail(string reason)
    {
        failures.Add((url, reason));
        Console.WriteLine($"    FAILED {path}: {reason}");
        if (++consecutiveFailures >= MaxConsecutiveFailures) throw new StopException($"{MaxConsecutiveFailures} failures in a row");
        return "";
    }
}

static string Clean(string html) =>
    WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Replace('\t', ' ').Trim();

class StopException(string message) : Exception(message);
