// FetchAnarchyLore.cs
// Downloads the archived full-size anarchylore.com screenshots (Files/FLO<id>_R.jpg) from the
// Wayback Machine, politely: one request at a time, 5-7 s apart, backing off on 429/5xx.
//
// Needs the .NET 10 SDK (a file-based app, no project file):
//   dotnet run FetchAnarchyLore.cs -- [imageresults.txt] [--out FOLDER] [--dry-run] [--limit N]
//
// Each file is saved as <first-seen date>_<original filename>, e.g. 2000-09-03_FLO103_R.jpg,
// where "first seen" is the earliest capture of ANY version (_T, _R or _F) of that screenshot ID.
// Files already on disk are skipped, so the run can be stopped with Ctrl+C and resumed.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

const string UserAgent =
    "AnarchyLoreScreenshotFetcher/1.0 (personal archival of anarchylore.com fan-site screenshots; " +
    "one request at a time, 5-7 s apart)";
const int MinDelayMs = 5000, MaxDelayMs = 7000;
const int MaxAttempts = 5;
const int SmallFileBytes = 5 * 1024;
const int MaxConsecutiveFailures = 3;

string inputPath = "imageresults.txt";
string? outDir = null;
bool dryRun = false;
int limit = int.MaxValue;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    if (a == "--dry-run") dryRun = true;
    else if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
    else if (a == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out limit) && limit > 0) i++;
    else if (a is "-h" or "--help") { PrintUsage(); return 0; }
    else if (!a.StartsWith('-')) inputPath = a;
    else { Console.Error.WriteLine($"Bad or incomplete option: {a}"); PrintUsage(); return 2; }
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Capture list not found: {Path.GetFullPath(inputPath)}");
    PrintUsage();
    return 2;
}
outDir = Path.GetFullPath(outDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath))!, "anarchylore_screenshots"));

// ---- Parse the CDX output: urlkey timestamp original mimetype status digest length ----
var floKey = new Regex(@"/files/flo(\d+)_([a-z]+)\.jpe?g$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
var rows = new List<CdxRow>();
foreach (string line in File.ReadLines(inputPath))
{
    string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (f.Length < 7 || f[1].Length < 8) continue;
    Match m = floKey.Match(f[0]);
    if (!m.Success) continue;
    string path = f[2].Split('?', '#')[0];
    rows.Add(new CdxRow(
        Id: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
        Suffix: m.Groups[2].Value.ToUpperInvariant(),
        Timestamp: f[1],
        OriginalUrl: f[2],
        FileName: path[(path.LastIndexOf('/') + 1)..],
        Length: long.TryParse(f[6], out long len) ? len : -1));
}

var firstSeen = rows.GroupBy(r => r.Id)
    .ToDictionary(g => g.Key, g => g.Select(r => r.Timestamp).Min(StringComparer.Ordinal)!);

var targets = rows.Where(r => r.Suffix == "R")
    .OrderBy(r => r.Id).ThenBy(r => r.Timestamp, StringComparer.Ordinal)
    .DistinctBy(r => r.Id)
    .Select(r => new Target(r, $"{FormatDate(firstSeen[r.Id])}_{r.FileName}"))
    .ToList();
var pending = targets.Where(t => !File.Exists(Path.Combine(outDir, t.Name))).ToList();
int toFetch = Math.Min(limit, pending.Count);

Console.WriteLine($"Capture list : {Path.GetFullPath(inputPath)}");
Console.WriteLine($"               {rows.Count} screenshot captures across {firstSeen.Count} IDs");
Console.WriteLine($"Full (_R)    : {targets.Count}   already on disk: {targets.Count - pending.Count}   to download now: {toFetch}");
Console.WriteLine($"Output folder: {outDir}");
Console.WriteLine($"Estimated time at ~6 s per request: {TimeSpan.FromSeconds(toFetch * 6.5):h\\:mm\\:ss} (more if the archive is slow)");
Console.WriteLine();

if (dryRun)
{
    foreach (var t in targets)
    {
        bool have = File.Exists(Path.Combine(outDir, t.Name));
        string note = t.Row.Length is > 0 and < SmallFileBytes ? "   <-- under 5 KB in CDX" : "";
        Console.WriteLine($"{(have ? "[have]" : "      ")} {t.Name,-26} {t.Row.Length,9:N0} B  {WaybackUrl(t.Row)}{note}");
    }
    Console.WriteLine();
    Console.WriteLine("Dry run only - nothing was downloaded.");
    return 0;
}

// ---- Download ----
Directory.CreateDirectory(outDir);
foreach (string stale in Directory.EnumerateFiles(outDir, "*.part")) File.Delete(stale);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine();
    Console.WriteLine("Ctrl+C - stopping...");
};

using var http = new HttpClient(new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{ Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

var failures = new List<(Target Target, string Reason)>();
var notes = new List<(string Name, string Note)>();
int downloaded = 0, attempted = 0, consecutiveFailures = 0;
string? stopReason = null;

try
{
    foreach (var t in pending)
    {
        if (attempted >= limit) break;
        if (attempted > 0) await Task.Delay(Random.Shared.Next(MinDelayMs, MaxDelayMs + 1), cts.Token);
        attempted++;

        string dest = Path.Combine(outDir, t.Name);
        Outcome r = await FetchAsync(t, dest, cts.Token);
        string prefix = $"[{attempted}/{toFetch}] {t.Name,-26}";
        if (r.Ok)
        {
            downloaded++;
            consecutiveFailures = 0;
            Console.WriteLine($"{prefix} ok  {r.Message}");
            foreach (string n in r.Notes!)
            {
                Console.WriteLine($"    note: {n}");
                notes.Add((t.Name, n));
            }
            continue;
        }

        failures.Add((t, r.Message));
        consecutiveFailures++;
        Console.WriteLine($"{prefix} FAILED  {r.Message}");
        if (r.RateLimited) { stopReason = "still rate-limited (HTTP 429) after all retries"; break; }
        if (consecutiveFailures >= MaxConsecutiveFailures) { stopReason = $"{MaxConsecutiveFailures} failures in a row"; break; }
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    stopReason = "interrupted with Ctrl+C";
}

// ---- Summary ----
Console.WriteLine();
Console.WriteLine("==== Summary ====");
int onDisk = targets.Count(t => File.Exists(Path.Combine(outDir, t.Name)));
Console.WriteLine($"Downloaded this run: {downloaded}   Failed: {failures.Count}   " +
                  $"Full screenshots on disk: {onDisk} of {targets.Count}");
if (stopReason is not null)
    Console.WriteLine($"Stopped early: {stopReason}. Run the same command again later to resume.");

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures (run again to retry them):");
    foreach (var (t, reason) in failures)
        Console.WriteLine($"  {t.Name,-26} {reason}\n  {"",-26} {WaybackUrl(t.Row)}");
}

var small = targets
    .Select(t => new FileInfo(Path.Combine(outDir, t.Name)))
    .Where(fi => fi.Exists && fi.Length < SmallFileBytes)
    .ToList();
if (small.Count > 0 || notes.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Flagged for a manual look:");
    foreach (var fi in small)
        Console.WriteLine($"  {fi.Name,-26} only {fi.Length:N0} bytes - may be an error image, not a real screenshot");
    foreach (var (name, note) in notes)
        Console.WriteLine($"  {name,-26} {note}");
}

return failures.Count > 0 || stopReason is not null ? 1 : 0;

// ---- Helpers ----

async Task<Outcome> FetchAsync(Target t, string dest, CancellationToken ct)
{
    string url = WaybackUrl(t.Row);
    string part = dest + ".part";
    for (int attempt = 1; ; attempt++)
    {
        string problem;
        bool rateLimited = false;
        TimeSpan? retryAfter = null;
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, ct);
            int code = (int)resp.StatusCode;
            if (code == 200)
            {
                byte[] body = await resp.Content.ReadAsByteArrayAsync(ct);
                string? type = resp.Content.Headers.ContentType?.MediaType;
                string? kind = ImageKind(body);
                if (kind is null)
                    return new Outcome(false, $"not an image (Content-Type {type ?? "none"}, {body.Length:N0} bytes, starts \"{Preview(body)}\")");

                await File.WriteAllBytesAsync(part, body, ct);
                File.Move(part, dest, overwrite: true);
                return new Outcome(true, $"{body.Length,9:N0} B  (CDX record {t.Row.Length:N0} B)", Notes: Check(t, body, kind, type));
            }
            if (code != 429 && code < 500)
                return new Outcome(false, $"HTTP {code} {resp.ReasonPhrase}");

            problem = $"HTTP {code} {resp.ReasonPhrase}";
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
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }

        if (attempt == MaxAttempts)
            return new Outcome(false, $"{problem} - gave up after {MaxAttempts} attempts", RateLimited: rateLimited);

        // Honor Retry-After when sent (capped at an hour); otherwise 60 s, doubling each time.
        TimeSpan wait = retryAfter is { } ra && ra > TimeSpan.Zero
            ? (ra < TimeSpan.FromHours(1) ? ra : TimeSpan.FromHours(1))
            : TimeSpan.FromSeconds(60 << (attempt - 1));
        wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000));
        Console.WriteLine($"    {t.Name}: {problem}; waiting {wait.TotalSeconds:0} s before attempt {attempt + 1} of {MaxAttempts}");
        await Task.Delay(wait, ct);
    }
}

// Sanity checks on a downloaded image. Files under 5 KB are flagged separately in the summary.
List<string> Check(Target t, byte[] body, string kind, string? type)
{
    var found = new List<string>();
    if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        found.Add($"served as {type} although the bytes are a {kind}");
    if (kind == "JPEG" && !HasJpegEnd(body))
        found.Add("no JPEG end marker - the archived image may be truncated");
    // The CDX length is the size of the compressed archive record (headers included), so it is
    // only roughly the image size; flag big differences.
    long cdx = t.Row.Length;
    if (cdx > 0 && (body.Length > cdx * 1.10 + 1024 || body.Length < cdx * 0.80 - 1024))
        found.Add($"size {body.Length:N0} B differs a lot from the CDX record length {cdx:N0} B");
    return found;
}

static string WaybackUrl(CdxRow r) => $"https://web.archive.org/web/{r.Timestamp}id_/{r.OriginalUrl}";

static string FormatDate(string ts) => $"{ts[..4]}-{ts[4..6]}-{ts[6..8]}";

static string? ImageKind(byte[] b) =>
    b is [0xFF, 0xD8, 0xFF, ..] ? "JPEG" :
    b is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] ? "PNG" :
    b is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] ? "GIF" :
    null;

static bool HasJpegEnd(byte[] b)
{
    // Allow a little trailing padding after the FF D9 end-of-image marker.
    for (int i = b.Length - 2; i >= Math.Max(0, b.Length - 64); i--)
        if (b[i] == 0xFF && b[i + 1] == 0xD9) return true;
    return false;
}

static string Preview(byte[] b)
{
    var sb = new StringBuilder();
    foreach (byte c in b.AsSpan(0, Math.Min(b.Length, 40)))
        sb.Append(c is >= 32 and < 127 ? (char)c : '.');
    return sb.ToString();
}

static void PrintUsage() => Console.Error.WriteLine("""

    Usage: dotnet run FetchAnarchyLore.cs -- [imageresults.txt] [--out FOLDER] [--dry-run] [--limit N]

      imageresults.txt  Wayback CDX capture list (default: imageresults.txt in the current folder)
      --out FOLDER      where to save screenshots (default: anarchylore_screenshots next to the list)
      --dry-run         show what would be downloaded, without contacting archive.org
      --limit N         download at most N files this run (for a quick test)
    """);

record CdxRow(int Id, string Suffix, string Timestamp, string OriginalUrl, string FileName, long Length);
record Target(CdxRow Row, string Name);
record Outcome(bool Ok, string Message, bool RateLimited = false, List<string>? Notes = null);
