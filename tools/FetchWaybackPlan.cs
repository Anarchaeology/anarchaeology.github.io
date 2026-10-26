// FetchWaybackPlan.cs
// Downloads a planned list of archived images from the Wayback Machine, politely, and dates each one.
// One request at a time, 5-7 s apart; on 429/5xx honors Retry-After or backs off 60 s, doubling.
// Already-downloaded URLs (listed in the manifest) are skipped, so the run can be stopped and resumed.
//
// Needs the .NET 10 SDK:
//   dotnet run FetchWaybackPlan.cs -- PLAN.tsv OUTFOLDER [--limit N] [--live]
//
// --live fetches each picture from its original (still running) site instead of the Wayback Machine;
// the plan's timestamp is then the day it was found there, and "server" is the site's own Last-Modified.
//
// PLAN.tsv columns (tab-separated, no header):
//   tier, source, wayback timestamp, original URL, mimetype, CDX length, normalized URL, forum date, forum thread
//
// Each image is saved as OUTFOLDER/<site>/<original folders>/<date>_<original name>, with its
// "Date modified" set to that date. The date is the earliest believable of:
//   embedded  - an EXIF-style "YYYY:MM:DD hh:mm:ss" stamp inside the file
//   path      - a date in the URL (e.g. screenshots/010617/ or sod/07182001.jpg)
//   server    - the original web server's Last-Modified header, which Wayback preserves
//   forum     - the date the image was first linked on the forum
//   capture   - the Wayback Machine's first capture (always available)
// OUTFOLDER/manifest.tsv lists every date source per file; OUTFOLDER/index.html is a contact sheet sorted by date.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

const string UserAgent =
    "AOWaybackPhotoFetcher/1.0 (personal archival of Anarchy Online fan-site screenshots; one request at a time, 5-7 s apart)";
const int MinDelayMs = 5000, MaxDelayMs = 7000;
const int MaxAttempts = 5;
const int MaxConsecutiveGiveUps = 3;
var earliestPlausible = new DateTime(1999, 1, 1);

string? planPath = null, outDir = null;
int limit = int.MaxValue;
bool redate = false, indexOnly = false, live = false;
string? rejectsList = null, duplicatesTable = null, datesTable = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out limit) && limit > 0) i++;
    else if (args[i] == "--live") live = true;
    else if (args[i] == "--redate") redate = true;
    else if (args[i] == "--index-only") indexOnly = true;
    else if (args[i] == "--apply-rejects" && i + 1 < args.Length) rejectsList = args[++i];
    else if (args[i] == "--apply-duplicates" && i + 1 < args.Length) duplicatesTable = args[++i];
    else if (args[i] == "--set-dates" && i + 1 < args.Length) datesTable = args[++i];
    else if (args[i].StartsWith('-')) { Console.Error.WriteLine($"Bad or incomplete option: {args[i]}"); return 2; }
    else if (planPath is null) planPath = args[i];
    else outDir = args[i];
}
if (planPath is null || outDir is null || !File.Exists(planPath))
{
    Console.Error.WriteLine("Usage: dotnet run FetchWaybackPlan.cs -- PLAN.tsv OUTFOLDER [--limit N | --redate | --index-only | --apply-rejects LIST.txt | --apply-duplicates duplicates.tsv | --set-dates TABLE.tsv]");
    return 2;
}
outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);

var plan = File.ReadLines(planPath)
    .Select(l => l.Split('\t'))
    .Where(f => f.Length >= 7)
    .Select(f => new PlanRow(f[0], f[2], f[3], long.TryParse(f[5], out long n) ? n : 0, f[6],
                             f.Length > 7 ? f[7] : "", f.Length > 8 ? f[8] : ""))
    .ToList();

// ---- Resume state ----
string manifestPath = Path.Combine(outDir, "manifest.tsv");
const string ManifestHeader =
    "date\tdate_source\tfile\ttier\tsite\toriginal_url\twayback_url\tcapture\tserver\tforum\tforum_thread\tpath\tembedded\tbytes";
var doneUrls = new HashSet<string>();
if (File.Exists(manifestPath))
    foreach (var f in File.ReadLines(manifestPath).Skip(1).Select(l => l.Split('\t')).Where(f => f.Length >= 6))
        if (File.Exists(Path.Combine(outDir, f[2]))) doneUrls.Add(f[5]);
// Images moved aside with --apply-rejects are never fetched again.
string rejectedPath = Path.Combine(outDir, "rejected.tsv");
if (File.Exists(rejectedPath))
    foreach (var f in File.ReadLines(rejectedPath).Skip(1).Select(l => l.Split('\t')).Where(f => f.Length >= 6))
        doneUrls.Add(f[5]);
if (redate) return Redate();
if (rejectsList is not null) return ApplyRejects(rejectsList);
if (duplicatesTable is not null) return ApplyDuplicates(duplicatesTable);
if (datesTable is not null) return SetDates(datesTable);
if (indexOnly) { WriteIndex(); Console.WriteLine($"Wrote {Path.Combine(outDir, "index.html")}"); return 0; }
var pending = plan.Where(r => !doneUrls.Contains(r.Url)).ToList();
int toFetch = Math.Min(limit, pending.Count);
Console.WriteLine($"Plan: {plan.Count} images   already downloaded: {plan.Count - pending.Count}   to fetch now: {toFetch}");
Console.WriteLine($"Output: {outDir}");
Console.WriteLine($"Estimated time: ~{TimeSpan.FromSeconds(toFetch * 7.5):h\\:mm} (one request every 5-7 s)");
Console.WriteLine();

foreach (string stale in Directory.EnumerateFiles(outDir, "*.part", SearchOption.AllDirectories)) File.Delete(stale);

bool newManifest = !File.Exists(manifestPath) || new FileInfo(manifestPath).Length == 0;
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var manifest = new StreamWriter(manifestPath, append: true, utf8) { AutoFlush = true };
if (newManifest) manifest.WriteLine(ManifestHeader);
WriteIndex();
using var failuresLog = new StreamWriter(Path.Combine(outDir, "failures.tsv"), append: true, utf8) { AutoFlush = true };

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nCtrl+C - stopping..."); };

using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
{ Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

int attempted = 0, downloaded = 0, consecutiveGiveUps = 0;
var failures = new List<(PlanRow Row, string Reason)>();
var sourceCounts = new Dictionary<string, int>();
string? stopReason = null;

// Keep Windows from idle-sleeping while downloading. The display may still turn off, and the request
// ends when this program exits; no power settings are changed.
if (OperatingSystem.IsWindows()) Power.SetThreadExecutionState(Power.Continuous | Power.SystemRequired);

try
{
    foreach (var row in pending)
    {
        if (attempted >= limit) break;
        if (attempted > 0) await Task.Delay(Random.Shared.Next(MinDelayMs, MaxDelayMs + 1), cts.Token);
        attempted++;

        var r = await FetchAsync(row, cts.Token);
        string prefix = $"[{attempted}/{toFetch}] t{row.Tier} {Shorten(row.Norm, 70),-70}";
        if (r.Body is null)
        {
            failures.Add((row, r.Error));
            failuresLog.WriteLine($"{row.Url}\t{r.Error}");
            Console.WriteLine($"{prefix} FAILED  {r.Error}");
            if (r.GaveUp)
            {
                if (r.RateLimited) { stopReason = "still rate-limited (HTTP 429) after all retries"; break; }
                if (++consecutiveGiveUps >= MaxConsecutiveGiveUps) { stopReason = $"{MaxConsecutiveGiveUps} give-ups in a row"; break; }
            }
            continue;
        }
        consecutiveGiveUps = 0;

        var (date, source, dates) = PickDate(row, r.Body, r.ServerDate);
        string rel = SaveFile(row, r.Body, date);
        sourceCounts[source] = sourceCounts.GetValueOrDefault(source) + 1;
        downloaded++;
        manifest.WriteLine(string.Join('\t',
            date.ToString("yyyy-MM-dd"), source, rel, row.Tier, row.Norm.Split('/')[0], row.Url, WaybackUrl(row),
            D(dates.Capture), D(dates.Server), D(dates.Forum), row.ForumThread, D(dates.Path), D(dates.Embedded),
            r.Body.Length.ToString(CultureInfo.InvariantCulture)));
        Console.WriteLine($"{prefix} ok  {r.Body.Length,9:N0} B  {date:yyyy-MM-dd} ({source})");
        if (downloaded % 50 == 0) WriteIndex();
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested) { stopReason = "interrupted with Ctrl+C"; }
finally
{
    WriteIndex();
    if (OperatingSystem.IsWindows()) Power.SetThreadExecutionState(Power.Continuous);
}

Console.WriteLine();
Console.WriteLine("==== Summary ====");
Console.WriteLine($"Downloaded this run: {downloaded}   Failed: {failures.Count}   " +
                  $"Total in manifest: {ManifestRows().Count} of {plan.Count}");
Console.WriteLine("Dates taken from: " + string.Join(", ", sourceCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
if (stopReason is not null) Console.WriteLine($"Stopped early: {stopReason}. Run the same command again to resume.");
if (failures.Count > 0)
{
    Console.WriteLine($"Failures ({failures.Count}, also in failures.tsv):");
    foreach (var (row, reason) in failures.Take(40)) Console.WriteLine($"  {Shorten(row.Norm, 70),-70} {reason}");
    if (failures.Count > 40) Console.WriteLine($"  ... and {failures.Count - 40} more");
}
Console.WriteLine($"Contact sheet: {Path.Combine(outDir, "index.html")}");
return stopReason is null ? 0 : 1;

// ---- Download with retries ----

async Task<Fetched> FetchAsync(PlanRow row, CancellationToken ct)
{
    string url = live ? row.Url : WaybackUrl(row);
    for (int attempt = 1; ; attempt++)
    {
        string problem;
        bool rateLimited = false;
        TimeSpan? retryAfter = null;
        // HttpClient.Timeout stops covering a request once its headers arrive, so give each attempt its
        // own 2-minute limit that also covers receiving the file.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            // Read headers first so a transfer that breaks off mid-body is caught separately below.
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
            int code = (int)resp.StatusCode;
            if (code == 200)
            {
                byte[] body;
                try { body = await resp.Content.ReadAsByteArrayAsync(attemptCts.Token); }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    // The archive answered but the transfer broke off or stalled. That usually means a damaged copy
                    // of this one file rather than rate limiting, so retry once after a short pause, then move on.
                    if (ex is OperationCanceledException) ex = new IOException("transfer stalled for 2 minutes");
                    if (attempt >= 2) return new Fetched(null, null, $"transfer broke off twice ({ex.Message})");
                    Console.WriteLine($"    {Shorten(row.Norm, 60)}: transfer broke off; retrying once in 30 s");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                if (ImageExtension(body) is null)
                    return new Fetched(null, null, $"not an image ({resp.Content.Headers.ContentType?.MediaType ?? "no type"}, {body.Length:N0} B)");
                DateTime? server = live ? resp.Content.Headers.LastModified?.UtcDateTime :
                    resp.Headers.TryGetValues("X-Archive-Orig-Last-Modified", out var v) &&
                    DateTimeOffset.TryParse(v.First(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm)
                    ? lm.UtcDateTime : null;
                return new Fetched(body, server, "");
            }
            if (code != 429 && code < 500) return new Fetched(null, null, $"HTTP {code}");
            problem = $"HTTP {code}";
            rateLimited = code == 429;
            retryAfter = resp.Headers.RetryAfter switch
            {
                { Delta: TimeSpan d } => d,
                { Date: DateTimeOffset when } => when - DateTimeOffset.UtcNow,
                _ => null,
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
        {
            problem = ex is TaskCanceledException ? "request timed out" : ex.Message;
        }

        if (attempt == MaxAttempts)
            return new Fetched(null, null, $"{problem} - gave up after {MaxAttempts} attempts", GaveUp: true, RateLimited: rateLimited);
        TimeSpan wait = retryAfter is { } ra && ra > TimeSpan.Zero
            ? (ra < TimeSpan.FromHours(1) ? ra : TimeSpan.FromHours(1))
            : TimeSpan.FromSeconds(60 << (attempt - 1));
        wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000));
        Console.WriteLine($"    {Shorten(row.Norm, 60)}: {problem}; waiting {wait.TotalSeconds:0} s before attempt {attempt + 1} of {MaxAttempts}");
        await Task.Delay(wait, ct);
    }
}

// ---- Dating ----

(DateTime Date, string Source, Dates All) PickDate(PlanRow row, byte[] body, DateTime? server)
{
    DateTime capture = ParseStamp(row.Timestamp);
    var all = new Dates(capture, server, ParseDay(row.ForumDate), PathDate(row.Norm), EmbeddedDate(body));
    var candidates = new (DateTime? Date, string Source)[]
        { (all.Embedded, "embedded"), (all.Path, "path"), (all.Server, "server"), (all.Forum, "forum"), (all.Capture, "capture") };
    var best = candidates
        .Where(c => c.Date is { } d && d >= earliestPlausible && d <= capture.AddDays(1))
        .OrderBy(c => c.Date!.Value.Date)
        .DefaultIfEmpty((capture, "capture"))
        .First();
    return (best.Date!.Value, best.Source, all);
}

// --redate: recompute every downloaded file's date with the current rules (no network), renaming as needed.
int Redate()
{
    if (!File.Exists(manifestPath)) { Console.WriteLine("No manifest yet."); return 0; }
    var rows = File.ReadAllLines(manifestPath).Skip(1).Select(l => l.Split('\t')).Where(f => f.Length >= 14).ToList();
    int changed = 0;
    foreach (var f in rows)
    {
        // Dates set by hand or by a later step (level, order, series, copy:...) are not recomputed.
        if (f[1] is not ("embedded" or "path" or "server" or "forum" or "capture")) continue;
        DateTime capture = ParseDay(f[7])!.Value;
        DateTime? path = PathDate(Regex.Replace(Regex.Replace(f[5], @"^[a-zA-Z]+://", ""), @"^www\.", "", RegexOptions.IgnoreCase).Replace(":80/", "/"));
        f[11] = D(path);
        var best = new (DateTime? Date, string Source)[]
                { (ParseDay(f[12]), "embedded"), (path, "path"), (ParseDay(f[8]), "server"), (ParseDay(f[9]), "forum"), (capture, "capture") }
            .Where(c => c.Date is { } d && d >= earliestPlausible && d <= capture.AddDays(1))
            .OrderBy(c => c.Date!.Value)
            .First();
        string newDate = best.Date!.Value.ToString("yyyy-MM-dd");
        if (newDate == f[0] && best.Source == f[1]) continue;

        string oldFull = Path.Combine(outDir!, f[2]);
        string newRel = Path.Combine(Path.GetDirectoryName(f[2])!, Regex.Replace(Path.GetFileName(f[2]), @"^\d{4}-\d{2}-\d{2}_", newDate + "_"));
        string newFull = Path.Combine(outDir!, newRel);
        if (!File.Exists(oldFull) || (newFull != oldFull && File.Exists(newFull))) continue;
        if (newFull != oldFull) File.Move(oldFull, newFull);
        File.SetLastWriteTime(newFull, best.Date.Value);
        Console.WriteLine($"  {f[0]} ({f[1]}) -> {newDate} ({best.Source})  {newRel}");
        (f[0], f[1], f[2]) = (newDate, best.Source, newRel);
        changed++;
    }
    File.WriteAllLines(manifestPath, rows.Select(f => string.Join('\t', f)).Prepend(ManifestHeader), new UTF8Encoding(false));
    WriteIndex();
    Console.WriteLine($"Re-dated {changed} of {rows.Count} files.");
    return 0;
}

static DateTime ParseStamp(string ts) =>
    DateTime.ParseExact(ts.PadRight(14, '0')[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture);

static DateTime? ParseDay(string s) =>
    DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

static DateTime? PathDate(string norm)
{
    string path = norm.Split('?')[0];
    // Stratics "screenshot of the day": sod/MMDDYYYY.jpg
    var m = Regex.Match(path, @"/sod/(\d{2})(\d{2})(\d{4})(?:_[a-z0-9]+)?\.", RegexOptions.IgnoreCase);
    if (m.Success) return MakeDate(m.Groups[3].Value, m.Groups[1].Value, m.Groups[2].Value);
    // IGN AO Vault "daily shot": dailyshot/MM-DD-YY.jpg
    m = Regex.Match(path, @"/dailyshot/(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])-(0\d)[a-z]?\.", RegexOptions.IgnoreCase);
    if (m.Success) return MakeDate("20" + m.Groups[3].Value, m.Groups[1].Value, m.Groups[2].Value);
    // A full year: 20010617, 2001-06-17, 2001_06_17
    m = Regex.Match(path, @"(?<!\d)((?:199|200)\d)[-_]?(0[1-9]|1[0-2])[-_]?(0[1-9]|[12]\d|3[01])(?!\d)");
    if (m.Success) return MakeDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    // A dated folder: /YYMMDD/ (e.g. screenshots/010617/)
    m = Regex.Match(path, @"/(99|0\d)(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])/");
    if (m.Success) return MakeDate((m.Groups[1].Value == "99" ? "19" : "20") + m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    // An upload timestamp in the file name (Unix seconds, 2001-2004), e.g. nano01024046271.jpg
    m = Regex.Match(path[(path.LastIndexOf('/') + 1)..], @"(\d{10})(?!\d)");
    if (m.Success && long.Parse(m.Groups[1].Value) is >= 978307200 and < 1104537600)
        return DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).UtcDateTime.Date;
    return null;
}

static DateTime? MakeDate(string y, string mo, string d) =>
    DateTime.TryParseExact($"{y}-{mo}-{d}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : null;

static DateTime? EmbeddedDate(byte[] body)
{
    // EXIF and many editors store "YYYY:MM:DD hh:mm:ss" near the start of the file.
    string head = Encoding.Latin1.GetString(body, 0, Math.Min(body.Length, 65536));
    var m = Regex.Match(head, @"((?:19|20)\d\d):(\d\d):(\d\d) (\d\d):(\d\d):(\d\d)");
    return m.Success ? MakeDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : null;
}

static string D(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

// ---- Saving ----

string SaveFile(PlanRow row, byte[] body, DateTime date)
{
    string[] parts = row.Norm.Split('?', 2);
    var segs = parts[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Safe).ToList();
    string name = segs.Count > 1 && !parts[0].EndsWith('/') ? segs[^1] : "index";
    var dirs = segs.Count > 1 && !parts[0].EndsWith('/') ? segs.Take(segs.Count - 1) : segs;
    if (parts.Length > 1) name += "_" + Safe(parts[1]);
    string ext = ImageExtension(body)!;
    if (!Regex.IsMatch(name, @"\.(jpe?g|gif|png|bmp|webp)$", RegexOptions.IgnoreCase)) name += ext;
    if (name.Length > 120) name = name[..(120 - ext.Length)] + ext;

    string dir = Path.Combine([outDir!, .. dirs]);
    Directory.CreateDirectory(dir);
    string file = Path.Combine(dir, $"{date:yyyy-MM-dd}_{name}");
    for (int n = 2; File.Exists(file); n++)
        file = Path.Combine(dir, $"{date:yyyy-MM-dd}_{Path.GetFileNameWithoutExtension(name)}_{n}{Path.GetExtension(name)}");

    File.WriteAllBytes(file + ".part", body);
    File.Move(file + ".part", file);
    File.SetLastWriteTime(file, date);
    return Path.GetRelativePath(outDir!, file);
}

static string Safe(string segment)
{
    string s = Uri.UnescapeDataString(segment);
    s = Regex.Replace(s, @"[<>:""/\\|?*\x00-\x1F]", "_").TrimEnd('.', ' ');
    return s.Length == 0 ? "_" : s;
}

static string? ImageExtension(byte[] b) =>
    b is [0xFF, 0xD8, 0xFF, ..] ? ".jpg" :
    b is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] ? ".png" :
    b is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] ? ".gif" :
    b is [(byte)'B', (byte)'M', ..] && b.Length > 1000 ? ".bmp" :
    b is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] ? ".webp" :
    null;

// With --live the picture is fetched from the site itself; there is no archived copy to point to.
string WaybackUrl(PlanRow r) => live ? "" : $"https://web.archive.org/web/{r.Timestamp}id_/{r.Url}";

static string Shorten(string s, int max) => s.Length <= max ? s : "..." + s[^(max - 3)..];

// ---- Contact sheet ----

// The manifest stays open for appending, so read it with a share mode that allows that.
List<string[]> ManifestRows()
{
    var rows = new List<string[]>();
    using var reader = new StreamReader(new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    reader.ReadLine();
    while (reader.ReadLine() is { } line) if (line.Split('\t') is { Length: >= 14 } f) rows.Add(f);
    return rows;
}

void WriteIndex()
{
    var items = ManifestRows()
        .OrderBy(f => f[0], StringComparer.Ordinal).ThenBy(f => f[2], StringComparer.OrdinalIgnoreCase).ToList();
    var sites = items.Select(f => f[4]).Distinct().Order().ToList();

    var sb = new StringBuilder();
    sb.Append("""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>AO Wayback Photos</title>
        <style>
        :root { --bg:#f3f4f1; --fg:#1d2321; --muted:#5d6763; --card:#fff; --line:#d5d9d5; --accent:#2f6f62; --bad:#b3362a }
        @media (prefers-color-scheme: dark) { :root { --bg:#121615; --fg:#e4e8e5; --muted:#97a19c; --card:#1b2120; --line:#2c3432; --accent:#7cc4b2; --bad:#ef7b6c } }
        [hidden] { display:none !important }
        body { margin:0; background:var(--bg); color:var(--fg); font:14px/1.4 system-ui, sans-serif }
        header { position:sticky; top:0; z-index:1; background:var(--bg); border-bottom:1px solid var(--line); padding:10px 16px; display:grid; gap:8px }
        .bar { display:flex; gap:10px; flex-wrap:wrap; align-items:center }
        h1 { font-size:17px; margin:0 } .muted { color:var(--muted) }
        select, button, .filebtn { font:inherit; padding:4px 8px; border:1px solid var(--line); background:var(--card); color:var(--fg); border-radius:4px }
        select { max-width:22rem } button, .filebtn { cursor:pointer } button:focus-visible, select:focus-visible, .mark:focus-within { outline:2px solid var(--accent) }
        .mark { display:inline-flex; gap:6px; align-items:center; padding:4px 10px; border:1px solid var(--accent); border-radius:4px; cursor:pointer; user-select:none }
        body.marking .mark { background:var(--accent); color:var(--bg) }
        #rejcount { color:var(--bad); font-weight:600 }
        main { padding:8px 16px 40px }
        h2 { font-size:15px; margin:22px 0 8px; color:var(--accent); font-variant-numeric:tabular-nums }
        .grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(190px, 1fr)); gap:10px }
        figure { margin:0; background:var(--card); border:1px solid var(--line); border-radius:4px; overflow:hidden }
        figure img { display:block; width:100%; height:140px; object-fit:cover; background:var(--line) }
        figcaption { padding:6px 8px; font-size:12px; color:var(--muted); overflow-wrap:anywhere }
        figcaption b { color:var(--fg); font-weight:600; font-variant-numeric:tabular-nums }
        figcaption .open { color:var(--accent) }
        body.marking figure { cursor:pointer }
        figure.rejected { outline:3px solid var(--bad); outline-offset:-3px }
        figure.rejected img { opacity:.25 }
        figure.rejected figcaption::before { content:"Marked for removal · "; color:var(--bad); font-weight:600 }
        #viewer { position:fixed; inset:0; z-index:10; background:#0b0d0d; color:#e8ecea; display:grid; grid-template-rows:auto 1fr }
        .vbar { display:flex; flex-wrap:wrap; gap:6px 16px; align-items:center; padding:8px 16px; background:#151918; border-bottom:1px solid #2a302e; font-size:13px }
        #vpos { font-variant-numeric:tabular-nums; font-weight:600 }
        #vinfo { color:#a9b3af; overflow-wrap:anywhere; min-width:0; flex:1 1 20rem }
        #vstate { color:#ff8a7a; font-weight:700 }
        .vkeys { color:#7f8a86 }
        #vclose { margin-left:auto; background:#232927; color:#e8ecea; border-color:#3a423f }
        #vstage { position:relative; overflow:hidden; min-height:0 }
        #vimg { position:absolute; inset:0; margin:auto; max-width:calc(100% - 16px); max-height:calc(100% - 16px) }
        #viewer.actual #vstage { overflow:auto }
        #viewer.actual #vimg { position:static; display:block; max-width:none; max-height:none; margin:8px auto }
        #viewer.marked #vimg { outline:4px solid #ff6b5b; opacity:.45 }
        .vnav { position:fixed; top:50%; transform:translateY(-50%); font-size:36px; line-height:1; background:rgba(0,0,0,.45); color:#fff; border:0; border-radius:4px; padding:10px 14px; cursor:pointer }
        #vprev { left:8px } #vnext { right:8px }
        </style></head><body>
        <header>
        <div class="bar"><h1>AO Wayback Photos</h1><span id="count" class="muted"></span><span id="rejcount"></span></div>
        <div class="bar">
        <label>Site <select id="site"><option value="">All sites</option>
        """);
    foreach (string s in sites) sb.Append($"<option>{WebUtility.HtmlEncode(s)}</option>");
    sb.Append("""
        </select></label>
        <label>Folder <select id="dir"></select></label>
        <label>Show <select id="show"><option value="all">All</option><option value="kept">Not marked</option><option value="marked">Marked</option></select></label>
        <button id="review" type="button">Review one by one</button>
        <label class="mark"><input type="checkbox" id="mark"> Mark mode (M)</label>
        <button id="markshown" type="button">Mark all shown</button>
        <button id="clearshown" type="button">Unmark all shown</button>
        <button id="export" type="button">Export marked list</button>
        <label class="filebtn">Import list <input type="file" id="import" accept=".txt" hidden></label>
        </div>
        <div class="muted">Click a picture to view it full size and step through with ← →; X marks it and moves on. In mark mode, clicks mark instead, and Shift+click marks a range.
        Marks are kept in this browser, even after the page rebuilds. To move marked pictures aside, export the list.
        Date = earliest of embedded / path / server / forum / capture / listed (a saved page already showed it), or an estimate: level (character level shown), order (file numbering), series, copy (another copy's date).</div>
        </header><main>
        """);

    foreach (var month in items.GroupBy(f => f[0][..7]))
    {
        sb.Append($"<section><h2>{month.Key}</h2><div class=\"grid\">");
        foreach (var f in month)
        {
            string rel = f[2].Replace('\\', '/');
            string href = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
            string dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
            sb.Append($"<figure data-url=\"{WebUtility.HtmlEncode(f[5])}\" data-site=\"{WebUtility.HtmlEncode(f[4])}\" data-dir=\"{WebUtility.HtmlEncode(dir)}\" data-file=\"{WebUtility.HtmlEncode(rel)}\" data-date=\"{f[0]}\" data-source=\"{f[1]}\">" +
                      $"<a href=\"{href}\" target=\"_blank\"><img loading=\"lazy\" src=\"{href}\" alt=\"\"></a>" +
                      $"<figcaption><b>{f[0]}</b> {f[1]} · <a class=\"open\" href=\"{href}\" target=\"_blank\">open</a><br>{WebUtility.HtmlEncode(rel)}</figcaption></figure>");
        }
        sb.Append("</div></section>");
    }
    sb.Append("""
        </main>
        <div id="viewer" hidden>
          <div class="vbar"><span id="vpos"></span><span id="vstate"></span><span id="vinfo"></span>
            <span class="vkeys">← → move · X mark or unmark, then next · Z actual size · Esc back to grid</span>
            <button id="vclose" type="button">Back to grid</button></div>
          <div id="vstage"><img id="vimg" alt=""></div>
          <button id="vprev" class="vnav" type="button" aria-label="Previous picture">‹</button>
          <button id="vnext" class="vnav" type="button" aria-label="Next picture">›</button>
        </div>
        <script>
        const KEY = 'aoWaybackRejects', UI = 'aoWaybackUi', LAST = 'aoWaybackLastViewed';
        const $ = id => document.getElementById(id);
        const figs = [...document.querySelectorAll('figure')], sections = [...document.querySelectorAll('section')];
        const siteSel = $('site'), dirSel = $('dir'), showSel = $('show'), mark = $('mark');
        let rejected = new Set(), lastFig = null;
        try { rejected = new Set(JSON.parse(localStorage.getItem(KEY) || '[]')); } catch (e) {}
        function save() { try { localStorage.setItem(KEY, JSON.stringify([...rejected])); } catch (e) {} }
        function saveUi() { try { localStorage.setItem(UI, JSON.stringify({ site: siteSel.value, dir: dirSel.value, show: showSel.value, mark: mark.checked })); } catch (e) {} }
        function fillDirs(wanted) {
          const dirs = [...new Set(figs.filter(f => !siteSel.value || f.dataset.site === siteSel.value).map(f => f.dataset.dir))].sort();
          dirSel.replaceChildren(new Option('All folders', ''), ...dirs.map(d => new Option(d, d)));
          dirSel.value = dirs.includes(wanted) ? wanted : '';
        }
        function shown(f) {
          if (siteSel.value && f.dataset.site !== siteSel.value) return false;
          if (dirSel.value && f.dataset.dir !== dirSel.value) return false;
          const r = rejected.has(f.dataset.url);
          return showSel.value === 'all' || (showSel.value === 'kept' ? !r : r);
        }
        function apply() {
          let n = 0, marked = 0;
          for (const f of figs) {
            const r = rejected.has(f.dataset.url);
            f.classList.toggle('rejected', r); if (r) marked++;
            f.hidden = !shown(f); if (!f.hidden) n++;
          }
          for (const s of sections) s.hidden = !s.querySelector('figure:not([hidden])');
          $('count').textContent = n + ' of ' + figs.length + ' shown';
          $('rejcount').textContent = marked ? marked + ' marked for removal' : '';
          document.body.classList.toggle('marking', mark.checked);
          saveUi();
        }
        function setMarked(list, value) { for (const f of list) value ? rejected.add(f.dataset.url) : rejected.delete(f.dataset.url); save(); apply(); }
        // ---- One-by-one viewer: steps through the pictures currently shown, in date order ----
        const viewer = $('viewer'), vimg = $('vimg');
        let vlist = [], vi = 0;
        const hrefOf = f => f.querySelector('a').getAttribute('href');
        function openViewer(start) {
          vlist = figs.filter(f => !f.hidden);
          if (!vlist.length) return;
          vi = Math.max(0, vlist.indexOf(start));
          if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
          viewer.hidden = false; document.body.style.overflow = 'hidden';
          showCurrent();
        }
        function showCurrent() {
          const f = vlist[vi], marked = rejected.has(f.dataset.url);
          viewer.classList.remove('actual');
          viewer.classList.toggle('marked', marked);
          vimg.src = hrefOf(f);
          $('vpos').textContent = (vi + 1) + ' / ' + vlist.length;
          $('vstate').textContent = marked ? 'Marked for removal' : '';
          $('vinfo').textContent = f.dataset.date + ' (' + f.dataset.source + ') · ' + f.dataset.file;
          try { localStorage.setItem(LAST, f.dataset.url); } catch (e) {}
          if (vlist[vi + 1]) new Image().src = hrefOf(vlist[vi + 1]);
        }
        vimg.onload = () => { $('vinfo').textContent = vlist[vi].dataset.date + ' (' + vlist[vi].dataset.source + ') · ' + vimg.naturalWidth + '×' + vimg.naturalHeight + ' · ' + vlist[vi].dataset.file; };
        function go(i) { if (i < 0 || i >= vlist.length || i === vi) return; vi = i; showCurrent(); }
        function toggleCurrent() {
          const f = vlist[vi];
          setMarked([f], !rejected.has(f.dataset.url));
          if (vi < vlist.length - 1) go(vi + 1); else showCurrent();
        }
        function closeViewer() {
          viewer.hidden = true; document.body.style.overflow = '';
          apply();
          const f = vlist[vi]; if (f && !f.hidden) f.scrollIntoView({ block: 'center' });
        }
        $('vprev').onclick = () => go(vi - 1);
        $('vnext').onclick = () => go(vi + 1);
        $('vclose').onclick = closeViewer;
        $('review').onclick = () => {
          let last = null; try { last = localStorage.getItem(LAST); } catch (e) {}
          openViewer(figs.find(f => !f.hidden && f.dataset.url === last) || figs.find(f => !f.hidden));
        };

        document.querySelector('main').addEventListener('click', e => {
          if (e.target.closest('.open') || e.ctrlKey || e.metaKey || e.shiftKey && !mark.checked || e.button !== 0) return;
          const f = e.target.closest('figure'); if (!f) return;
          e.preventDefault();
          if (!mark.checked) { openViewer(f); return; }
          const value = !rejected.has(f.dataset.url);
          const vis = figs.filter(x => !x.hidden), i = vis.indexOf(f), j = lastFig ? vis.indexOf(lastFig) : -1;
          setMarked(e.shiftKey && j >= 0 ? vis.slice(Math.min(i, j), Math.max(i, j) + 1) : [f], value);
          lastFig = f;
        });
        $('markshown').onclick = () => setMarked(figs.filter(f => !f.hidden), true);
        $('clearshown').onclick = () => setMarked(figs.filter(f => !f.hidden), false);
        $('export').onclick = () => {
          const files = new Map(figs.map(f => [f.dataset.url, f.dataset.file]));
          const text = [...rejected].map(u => u + '\t' + (files.get(u) || '')).join('\n') + '\n';
          const a = document.createElement('a');
          a.href = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
          a.download = 'ao_wayback_rejects.txt'; document.body.append(a); a.click(); a.remove();
          setTimeout(() => URL.revokeObjectURL(a.href), 2000);
        };
        $('import').onchange = async e => {
          const file = e.target.files[0]; if (!file) return;
          for (const line of (await file.text()).split(/\r?\n/)) { const u = line.split('\t')[0].trim(); if (u) rejected.add(u); }
          e.target.value = ''; save(); apply();
        };
        siteSel.onchange = () => { fillDirs(''); apply(); };
        dirSel.onchange = showSel.onchange = mark.onchange = apply;
        document.addEventListener('keydown', e => {
          if (e.ctrlKey || e.metaKey || e.altKey) return;
          const k = e.key.toLowerCase();
          if (!viewer.hidden) {
            if (k === 'arrowright' || k === 'arrowdown' || k === 'd') go(vi + 1);
            else if (k === 'arrowleft' || k === 'arrowup' || k === 'a') go(vi - 1);
            else if (k === 'x' || k === ' ') toggleCurrent();
            else if (k === 'z') viewer.classList.toggle('actual');
            else if (k === 'home') go(0);
            else if (k === 'end') go(vlist.length - 1);
            else if (k === 'escape') closeViewer();
            else return;
            e.preventDefault();
            return;
          }
          if (k !== 'm') return;
          if (e.target instanceof Element && e.target.closest('select, input')) return;
          mark.checked = !mark.checked; apply();
        });
        let ui = {};
        try { ui = JSON.parse(localStorage.getItem(UI) || '{}'); } catch (e) {}
        if ([...siteSel.options].some(o => o.value === ui.site)) siteSel.value = ui.site;
        if (ui.show) showSel.value = ui.show;
        mark.checked = !!ui.mark;
        fillDirs(ui.dir || '');
        apply();
        </script></body></html>
        """);
    // Browsers share storage between local pages, so a collection in another folder keeps its own marks,
    // title and export name; the main collection keeps the original ones so existing marks stay.
    string folder = Path.GetFileName(outDir!);
    if (folder != "ao_wayback_photos")
    {
        sb.Replace("'aoWaybackRejects', UI = 'aoWaybackUi', LAST = 'aoWaybackLastViewed'",
                   $"'aoWaybackRejects:{folder}', UI = 'aoWaybackUi:{folder}', LAST = 'aoWaybackLastViewed:{folder}'");
        sb.Replace("'ao_wayback_rejects.txt'", $"'{folder}_rejects.txt'");
        sb.Replace("AO Wayback Photos</", $"AO Wayback Photos · {WebUtility.HtmlEncode(folder)}</");
    }
    File.WriteAllText(Path.Combine(outDir!, "index.html"), sb.ToString());
}

// --apply-rejects LIST: moves the listed images into OUTFOLDER\_rejected\ (same sub-folders), takes them out of
// the manifest and index, and records them in rejected.tsv so they are never downloaded again. Nothing is deleted.
int ApplyRejects(string listPath)
{
    if (!File.Exists(listPath)) { Console.Error.WriteLine($"List not found: {listPath}"); return 2; }
    if (!ManifestFree()) return 1;
    var urls = File.ReadLines(listPath).Select(l => l.Split('\t')[0].Trim()).Where(u => u.Length > 0).ToHashSet();
    MoveAside(urls, ManifestRows());
    return 0;
}

// --apply-duplicates duplicates.tsv (from FindDuplicates.cs): moves every "dup" aside like --apply-rejects, and
// gives each kept copy the earliest date found in its group (date source "copy:<how that copy was dated>").
// --set-dates TABLE: gives pictures new dates from a table with columns file, url, ..., new_date (7th),
// new_source (8th), e.g. the output of DateMiaJong.cs. Renames each file's date prefix, sets its file date,
// and updates the manifest and index.
int SetDates(string tsvPath)
{
    if (!File.Exists(tsvPath)) { Console.Error.WriteLine($"Dates table not found: {tsvPath}"); return 2; }
    if (!ManifestFree()) return 1;
    var rows = ManifestRows();
    var byUrl = rows.GroupBy(f => f[5]).ToDictionary(g => g.Key, g => g.First());
    int changed = 0, notFound = 0, skipped = 0;
    foreach (var t in File.ReadLines(tsvPath).Skip(1).Select(l => l.Split('\t')).Where(t => t.Length >= 8))
    {
        if (!byUrl.TryGetValue(t[1], out var row)) { notFound++; continue; }
        if (ParseDay(t[6]) is not { } day) { skipped++; continue; }
        if (t[6] == row[0] && t[7] == row[1]) continue;
        string newRel = Path.Combine(Path.GetDirectoryName(row[2])!,
            Regex.Replace(Path.GetFileName(row[2]), @"^\d{4}-\d{2}-\d{2}_", t[6] + "_"));
        string from = Path.Combine(outDir!, row[2]), to = Path.Combine(outDir!, newRel);
        if (!File.Exists(from) || (to != from && File.Exists(to))) { Console.WriteLine($"  skipped: {row[2]}"); skipped++; continue; }
        if (to != from) File.Move(from, to);
        File.SetLastWriteTime(to, day);
        (row[0], row[1], row[2]) = (t[6], t[7], newRel);
        changed++;
    }
    File.WriteAllLines(manifestPath, rows.Select(f => string.Join('\t', f)).Prepend(ManifestHeader), new UTF8Encoding(false));
    WriteIndex();
    Console.WriteLine($"Changed {changed} dates   not in the collection: {notFound}   skipped: {skipped}");
    return 0;
}

int ApplyDuplicates(string tsvPath)
{
    if (!File.Exists(tsvPath)) { Console.Error.WriteLine($"Duplicates table not found: {tsvPath}"); return 2; }
    if (!ManifestFree()) return 1;
    var rows = ManifestRows();
    var byUrl = rows.GroupBy(f => f[5]).ToDictionary(g => g.Key, g => g.First());
    var table = File.ReadLines(tsvPath).Skip(1).Select(l => l.Split('\t')).Where(f => f.Length >= 12).ToList();
    int redated = 0;
    foreach (var group in table.GroupBy(f => f[0]))
    {
        var members = group.Select(f => byUrl.GetValueOrDefault(f[11])).OfType<string[]>().ToList();
        var keeper = group.Where(f => f[1] == "keep").Select(f => byUrl.GetValueOrDefault(f[11])).OfType<string[]>().FirstOrDefault();
        if (keeper is null || members.Count < 2) continue;
        var earliest = members.OrderBy(m => m[0], StringComparer.Ordinal).First();
        if (string.CompareOrdinal(earliest[0], keeper[0]) >= 0) continue;

        string newRel = Path.Combine(Path.GetDirectoryName(keeper[2])!,
            Regex.Replace(Path.GetFileName(keeper[2]), @"^\d{4}-\d{2}-\d{2}_", earliest[0] + "_"));
        string from = Path.Combine(outDir!, keeper[2]), to = Path.Combine(outDir!, newRel);
        if (!File.Exists(from) || (to != from && File.Exists(to))) continue;
        if (to != from) File.Move(from, to);
        File.SetLastWriteTime(to, ParseDay(earliest[0])!.Value);
        Console.WriteLine($"  {keeper[0]} -> {earliest[0]}  {newRel}");
        string how = earliest[1].StartsWith("copy:") ? earliest[1] : "copy:" + earliest[1];
        (keeper[0], keeper[1], keeper[2]) = (earliest[0], how, newRel);
        redated++;
    }
    Console.WriteLine($"Gave {redated} kept copies an earlier date from one of their duplicates.");
    MoveAside(table.Where(f => f[1] == "dup").Select(f => f[11]).ToHashSet(), rows);
    return 0;
}

// The downloader keeps the manifest open; these steps rewrite it, so they need it to be stopped.
bool ManifestFree()
{
    if (!File.Exists(manifestPath)) { Console.Error.WriteLine("No manifest yet."); return false; }
    try { using var probe = new FileStream(manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
    catch (IOException) { Console.Error.WriteLine("The downloader is using the manifest. Stop it first; it resumes safely afterwards."); return false; }
}

// Moves the listed images into OUTFOLDER\_rejected\ (same sub-folders), records them in rejected.tsv so they are
// never downloaded again, and rewrites the manifest (with any other changes already made to rows) and the index.
void MoveAside(HashSet<string> urls, List<string[]> rows)
{
    var keep = new List<string[]>();
    var moved = new List<string[]>();
    foreach (var f in rows)
    {
        if (!urls.Contains(f[5])) { keep.Add(f); continue; }
        string rel = Path.Combine("_rejected", f[2]);
        string from = Path.Combine(outDir!, f[2]), to = Path.Combine(outDir!, rel);
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
        }
        f[2] = rel;
        moved.Add(f);
    }
    var noBom = new UTF8Encoding(false);
    string[] header = File.Exists(rejectedPath) ? [] : [ManifestHeader];
    File.AppendAllLines(rejectedPath, header.Concat(moved.Select(f => string.Join('\t', f))), noBom);
    File.WriteAllLines(manifestPath, keep.Select(f => string.Join('\t', f)).Prepend(ManifestHeader), noBom);
    WriteIndex();
    Console.WriteLine($"Moved {moved.Count} images to {Path.Combine(outDir!, "_rejected")}. " +
                      $"{urls.Count - moved.Count} listed addresses were not in the manifest (already moved, or not downloaded yet).");
}

static class Power
{
    public const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
}

record PlanRow(string Tier, string Timestamp, string Url, long Length, string Norm, string ForumDate, string ForumThread);
record Fetched(byte[]? Body, DateTime? ServerDate, string Error, bool GaveUp = false, bool RateLimited = false);
record Dates(DateTime Capture, DateTime? Server, DateTime? Forum, DateTime? Path, DateTime? Embedded);
