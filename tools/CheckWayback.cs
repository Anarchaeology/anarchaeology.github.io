// CheckWayback.cs
// For each URL prefix in a list, asks the Wayback CDX API which images it archived there.
// One request at a time, 5-7 s apart, backing off on 429/5xx; answers are cached, so re-runs
// only query new prefixes.
//
//   dotnet run CheckWayback.cs -- PREFIXES.txt OUTFOLDER
//
// PREFIXES.txt: one prefix per line, e.g. "multimania.com/nibel1/" (text after a tab is a note).
// Writes OUTFOLDER/cdx/<prefix>.txt (raw CDX lines, same format as imageresults.txt) and
// OUTFOLDER/summary.tsv (prefix, images, of which >= 10 KB, earliest, latest, note).

using System.Net;
using System.Text.RegularExpressions;

const string UserAgent =
    "AOForumArchiveSurvey/1.0 (personal research on Anarchy Online history; one request at a time, 5-7 s apart)";
const int MinDelayMs = 5000, MaxDelayMs = 7000;
const int MaxAttempts = 5;

if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet run CheckWayback.cs -- PREFIXES.txt OUTFOLDER"); return 2; }
var prefixes = File.ReadLines(args[0])
    .Select(l => l.Split('\t'))
    .Where(f => f[0].Trim().Length > 0 && !f[0].StartsWith('#'))
    .Select(f => (Prefix: f[0].Trim(), Note: f.Length > 1 ? f[1].Trim() : ""))
    .DistinctBy(p => p.Prefix)
    .ToList();
string cdxDir = Path.Combine(args[1], "cdx");
Directory.CreateDirectory(cdxDir);

using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
{ Timeout = TimeSpan.FromSeconds(180) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

var summary = new List<string> { "prefix\timages\tover_10KB\tearliest\tlatest\tnote" };
int requests = 0;
// Keep Windows from idle-sleeping while checking (the display may still turn off); ends when the program exits.
if (OperatingSystem.IsWindows()) Power.SetThreadExecutionState(Power.Continuous | Power.SystemRequired);
foreach (var (prefix, note) in prefixes)
{
    string file = Path.Combine(cdxDir, Regex.Replace(prefix, @"[^\w.-]+", "_") + ".txt");
    if (!File.Exists(file))
    {
        if (requests++ > 0) await Task.Delay(Random.Shared.Next(MinDelayMs, MaxDelayMs + 1));
        string url = "https://web.archive.org/cdx/search/cdx?url=" + Uri.EscapeDataString(prefix + "*") +
                     "&filter=mimetype:image/.*&filter=statuscode:200&collapse=urlkey&limit=20000";
        string? body = await GetAsync(url, prefix);
        if (body is null) { summary.Add($"{prefix}\tERROR\t\t\t\t{note}"); continue; }
        File.WriteAllText(file + ".part", body);
        File.Move(file + ".part", file, overwrite: true);
    }

    var rows = File.ReadLines(file).Select(l => l.Split(' ')).Where(f => f.Length >= 7).ToList();
    int big = rows.Count(f => long.TryParse(f[6], out long n) && n >= 10_000);
    string earliest = rows.Select(f => f[1]).DefaultIfEmpty("").Min(StringComparer.Ordinal)!;
    string latest = rows.Select(f => f[1]).DefaultIfEmpty("").Max(StringComparer.Ordinal)!;
    summary.Add($"{prefix}\t{rows.Count}\t{big}\t{Day(earliest)}\t{Day(latest)}\t{note}");
    Console.WriteLine($"{prefix,-55} {rows.Count,5} images ({big} >= 10 KB)");
}
File.WriteAllLines(Path.Combine(args[1], "summary.tsv"), summary);
return 0;

async Task<string?> GetAsync(string url, string label)
{
    for (int attempt = 1; ; attempt++)
    {
        string problem;
        TimeSpan? retryAfter = null;
        try
        {
            using var resp = await http.GetAsync(url);
            int code = (int)resp.StatusCode;
            if (code == 200) return await resp.Content.ReadAsStringAsync();
            if (code != 429 && code < 500) { Console.WriteLine($"{label}: HTTP {code}"); return null; }
            problem = $"HTTP {code}";
            retryAfter = resp.Headers.RetryAfter?.Delta;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            problem = ex is TaskCanceledException ? "timed out" : ex.Message;
        }
        if (attempt == MaxAttempts) { Console.WriteLine($"{label}: {problem}, gave up"); return null; }
        TimeSpan wait = retryAfter ?? TimeSpan.FromSeconds(60 << (attempt - 1));
        Console.WriteLine($"    {label}: {problem}; waiting {wait.TotalSeconds:0} s");
        await Task.Delay(wait);
    }
}

static string Day(string ts) => ts.Length >= 8 ? $"{ts[..4]}-{ts[4..6]}-{ts[6..8]}" : "";

static class Power
{
    public const uint Continuous = 0x80000000, SystemRequired = 0x00000001;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
}
