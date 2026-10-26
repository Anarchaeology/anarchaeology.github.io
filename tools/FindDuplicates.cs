// FindDuplicates.cs
// Finds pictures in a FetchWaybackPlan download folder that are the same image saved more than once:
// exact copies, and resized or re-saved versions (thumbnail next to full size, recompressed copies).
//
// Needs the .NET 10 SDK on Windows:
//   dotnet run FindDuplicates.cs -- OUTFOLDER [--hamming N] [--diff N] [--fine N]
//
// How two pictures count as the same image:
//   exact     - identical bytes
//   resized   - same width/height ratio (within 6%), a 64-bit difference hash (9x8 grey, each pixel compared
//               with its right neighbour) differing in at most --hamming bits (default 10), an average
//               difference of at most --diff grey levels (default 10 of 255) between 64x64 grey versions,
//               no 8x8 block of those differing by more than 16, and - compared at the smaller picture's own
//               resolution - no 8x8 block differing by more than --fine (default 35). That last check is what
//               tells a resize apart from screenshots of the same window with different numbers in it.
//               Nearly blank pictures are only matched as exact copies.
//   maybe     - pairs scoring between --fine and 60 are shown for a human decision, not listed.
// In each group the largest picture (most pixels, then biggest file) is kept.
//
// Writes, in OUTFOLDER:
//   duplicates.html      - every group side by side, keeper first, for checking by eye
//   duplicates_list.txt  - the non-keepers, in the format index.html's "Import list" reads
//   duplicates.tsv       - the same details as a table, including each group's earliest date

#:package System.Drawing.Common@9.0.0
#:property NoWarn=CA1416

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

string? dir = null;
int maxHamming = 10;
double maxDiff = 10, maxFine = 35, maxMaybe = 60;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--hamming" && i + 1 < args.Length && int.TryParse(args[i + 1], out maxHamming)) i++;
    else if (args[i] == "--fine" && i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out maxFine)) i++;
    else if (args[i] == "--diff" && i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out maxDiff)) i++;
    else if (!args[i].StartsWith('-')) dir = args[i];
    else { Console.Error.WriteLine($"Unknown option: {args[i]}"); return 2; }
}
if (dir is null || !File.Exists(Path.Combine(dir, "manifest.tsv")))
{
    Console.Error.WriteLine("Usage: dotnet run FindDuplicates.cs -- OUTFOLDER [--hamming N] [--diff N] [--fine N]");
    return 2;
}
dir = Path.GetFullPath(dir);

// The downloader may be appending to the manifest, so read it with a share mode that allows that.
var rows = new List<string[]>();
using (var reader = new StreamReader(new FileStream(Path.Combine(dir, "manifest.tsv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
{
    reader.ReadLine();
    while (reader.ReadLine() is { } line) if (line.Split('\t') is { Length: >= 14 } f) rows.Add(f);
}

// ---- Fingerprint every picture ----
var pics = new List<Pic>();
var unreadable = new List<string>();
foreach (var f in rows)
{
    string path = Path.Combine(dir, f[2]);
    if (!File.Exists(path)) continue;
    try
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var img = Image.FromStream(ms);
        byte[] g98 = Gray(img, 9, 8), g64 = Gray(img, 64, 64);
        ulong dhash = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                dhash = (dhash << 1) | (g98[y * 9 + x] > g98[y * 9 + x + 1] ? 1UL : 0UL);
        double mean = g64.Average(b => (double)b);
        double spread = Math.Sqrt(g64.Average(b => (b - mean) * (b - mean)));
        pics.Add(new Pic(f[0], f[1], f[2], f[5], img.Width, img.Height, bytes.Length,
                         Convert.ToHexString(MD5.HashData(bytes)), dhash, g64, spread));
    }
    catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException or IOException)
    {
        unreadable.Add(f[2]);
    }
    if (pics.Count % 250 == 0) Console.Write($"\r  fingerprinted {pics.Count} of {rows.Count}");
}
Console.WriteLine($"\r  fingerprinted {pics.Count} of {rows.Count} ({unreadable.Count} unreadable)");

// ---- Compare every pair; join matches into groups ----
int n = pics.Count;
var parent = Enumerable.Range(0, n).ToArray();
int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
var match = new Dictionary<(int, int), (string Kind, int Bits, double Diff, double Block)>();
var candidates = new List<string> { "bits\tdiff\tblock\tfine\taccepted\tfile_a\tfile_b" };
var closePairs = new List<(int I, int J, int Bits, double Diff, double Block)>();
var maybePairs = new List<(int I, int J, double Fine)>();
for (int i = 0; i < n; i++)
{
    var a = pics[i];
    for (int j = i + 1; j < n; j++)
    {
        var b = pics[j];
        if (a.Md5 == b.Md5) { Join(i, j, "exact", 0, 0, 0); continue; }
        if (a.Spread < 8 || b.Spread < 8) continue;
        int bits = BitOperations.PopCount(a.DHash ^ b.DHash);
        if (bits > maxHamming) continue;
        double ratio = (double)a.W / a.H / ((double)b.W / b.H);
        if (ratio < 0.94 || ratio > 1 / 0.94) continue;
        double diff = 0;
        for (int k = 0; k < a.G64.Length; k++) diff += Math.Abs(a.G64[k] - b.G64[k]);
        diff /= a.G64.Length;
        if (diff > maxDiff) continue;
        // Pictures built on the same template (same frame, different creature or text) agree almost
        // everywhere; a resize or re-save of one picture agrees everywhere. So also reject the pair if
        // any 8x8 block of the 64x64 versions differs a lot...
        double block = BlockMax(a.G64, b.G64, 64, 64);
        if (block > 16) { candidates.Add(Candidate(bits, diff, block, -1, false, a, b)); continue; }
        closePairs.Add((i, j, bits, diff, block));
    }
}
// ...and, for the pairs left, compare at the smaller picture's own resolution, where changed digits or
// small details still show up as a strongly differing block.
foreach (var (i, j, bits, diff, block) in closePairs)
{
    double fine = FineBlockMax(Path.Combine(dir, pics[i].File), Path.Combine(dir, pics[j].File));
    bool ok = fine <= maxFine;
    candidates.Add(Candidate(bits, diff, block, fine, ok, pics[i], pics[j]));
    if (ok) Join(i, j, "resized", bits, diff, fine);
    else if (fine <= maxMaybe) maybePairs.Add((i, j, fine));
}
maybePairs.RemoveAll(p => Find(p.I) == Find(p.J));   // already joined through other matches
string Candidate(int bits, double diff, double block, double fine, bool ok, Pic a, Pic b) =>
    string.Join('\t', bits, diff.ToString("0.0", CultureInfo.InvariantCulture), block.ToString("0.0", CultureInfo.InvariantCulture),
                fine < 0 ? "" : fine.ToString("0.0", CultureInfo.InvariantCulture), ok ? "yes" : "no", a.File, b.File);
void Join(int i, int j, string kind, int bits, double diff, double block) { parent[Find(i)] = Find(j); match[(i, j)] = (kind, bits, diff, block); }

var groups = Enumerable.Range(0, n).GroupBy(Find).Where(g => g.Count() > 1)
    .Select(g => g.Select(i => (Index: i, Pic: pics[i]))
                  .OrderByDescending(p => (long)p.Pic.W * p.Pic.H).ThenByDescending(p => p.Pic.Bytes)
                  .ThenBy(p => p.Pic.Date, StringComparer.Ordinal).ToList())
    .OrderBy(g => g.Min(p => p.Pic.Date), StringComparer.Ordinal)
    .ToList();

// Closest match between a member and anyone else in its group, for display.
(string Kind, int Bits, double Diff, double Block) Closest(List<(int Index, Pic Pic)> g, int i) =>
    g.Where(o => o.Index != i)
     .Select(o => match.TryGetValue((Math.Min(i, o.Index), Math.Max(i, o.Index)), out var m) ? m : (Kind: "", Bits: 99, Diff: 999.0, Block: 999.0))
     .OrderBy(m => m.Block).ThenBy(m => m.Bits).First();

// ---- Outputs ----
var list = new List<string>();
var table = new List<string> { "group\trole\tdate\tgroup_earliest\twidth\theight\tbytes\tmatch\tbits\tdiff\tfile\turl" };
var html = new StringBuilder();
html.Append("""
    <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <title>AO Wayback Duplicates</title>
    <style>
    :root { --bg:#f3f4f1; --fg:#1d2321; --muted:#5d6763; --card:#fff; --line:#d5d9d5; --keep:#2f6f62; --dup:#b3362a }
    @media (prefers-color-scheme: dark) { :root { --bg:#121615; --fg:#e4e8e5; --muted:#97a19c; --card:#1b2120; --line:#2c3432; --keep:#7cc4b2; --dup:#ef7b6c } }
    body { margin:0; background:var(--bg); color:var(--fg); font:14px/1.4 system-ui, sans-serif }
    header, main { padding:12px 16px }
    header { border-bottom:1px solid var(--line) } h1 { font-size:18px; margin:0 0 4px } .muted { color:var(--muted) }
    section { border-bottom:1px solid var(--line); padding:12px 0 }
    h2 { font-size:14px; margin:0 0 8px; font-variant-numeric:tabular-nums }
    .row { display:flex; gap:10px; flex-wrap:wrap; align-items:flex-start }
    figure { margin:0; background:var(--card); border:2px solid var(--line); border-radius:4px; padding:6px; max-width:330px }
    figure.keep { border-color:var(--keep) } figure.dup { border-color:var(--dup) }
    figure img { display:block; max-width:316px; max-height:220px; margin:0 auto }
    figcaption { font-size:12px; color:var(--muted); margin-top:4px; overflow-wrap:anywhere }
    figcaption b { font-weight:700 } .keep b { color:var(--keep) } .dup b { color:var(--dup) }
    </style></head><body><main>
    """);

int dupCount = 0, redates = 0, exactGroups = 0;
foreach (var (g, gi) in groups.Select((g, i) => (g, i + 1)))
{
    string earliest = g.Min(p => p.Pic.Date)!;
    var keeper = g[0];
    bool allExact = g.Skip(1).All(p => Closest(g, p.Index).Kind == "exact");
    if (allExact) exactGroups++;
    if (string.CompareOrdinal(earliest, keeper.Pic.Date) < 0) redates++;
    html.Append($"<section><h2>Group {gi} · {g.Count} copies · earliest date {earliest}" +
                (string.CompareOrdinal(earliest, keeper.Pic.Date) < 0 ? $" (the kept copy is dated {keeper.Pic.Date})" : "") + "</h2><div class=\"row\">");
    foreach (var (p, role) in g.Select((p, k) => (p, k == 0 ? "keep" : "dup")))
    {
        var m = role == "keep" ? (Kind: "", Bits: 0, Diff: 0.0, Block: 0.0) : Closest(g, p.Index);
        string rel = p.Pic.File.Replace('\\', '/');
        string href = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
        string how = m.Kind == "exact" ? "exact copy" : m.Kind == "resized" ? $"{m.Bits} bits apart, diff {m.Diff:0.0}, worst block {m.Block:0.0}" : "";
        html.Append($"<figure class=\"{role}\"><a href=\"{href}\" target=\"_blank\"><img loading=\"lazy\" src=\"{href}\" alt=\"\"></a><figcaption>" +
                    $"<b>{(role == "keep" ? "Keep" : "Duplicate")}</b> · {p.Pic.W}×{p.Pic.H} · {p.Pic.Bytes / 1024.0:0} KB · {p.Pic.Date} ({p.Pic.Source})" +
                    (how.Length > 0 ? $" · {how}" : "") + $"<br>{WebUtility.HtmlEncode(rel)}</figcaption></figure>");
        table.Add(string.Join('\t', gi, role, p.Pic.Date, earliest, p.Pic.W, p.Pic.H, p.Pic.Bytes, m.Kind, m.Bits,
                              m.Diff.ToString("0.0", CultureInfo.InvariantCulture), p.Pic.File, p.Pic.Url));
        if (role == "dup") { list.Add($"{p.Pic.Url}\t{rel}"); dupCount++; }
    }
    html.Append("</div></section>");
}
if (maybePairs.Count > 0)
{
    html.Append($"<h2 style=\"margin-top:28px\">Maybe the same picture ({maybePairs.Count} pairs, not in the list)</h2>" +
                "<p class=\"muted\">These pass the rough checks but differ in places: often a crop or a differently made thumbnail, " +
                "sometimes two different pictures. If one is a duplicate, mark it yourself in index.html.</p>");
    foreach (var (i, j, fine) in maybePairs.OrderBy(p => p.Fine))
    {
        html.Append($"<section><h2>Detail score {fine:0.0}</h2><div class=\"row\">");
        foreach (var p in new[] { pics[i], pics[j] })
        {
            string rel = p.File.Replace('\\', '/');
            string href = string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
            html.Append($"<figure><a href=\"{href}\" target=\"_blank\"><img loading=\"lazy\" src=\"{href}\" alt=\"\"></a><figcaption>" +
                        $"{p.W}×{p.H} · {p.Bytes / 1024.0:0} KB · {p.Date} ({p.Source})<br>{WebUtility.HtmlEncode(rel)}</figcaption></figure>");
        }
        html.Append("</div></section>");
    }
}
html.Append("</main></body></html>");
string intro = $"""
    <header><h1>AO Wayback Duplicates</h1>
    <div class="muted">{groups.Count} groups · {dupCount} duplicates · {maybePairs.Count} maybe-pairs at the bottom · checked {pics.Count} pictures.
    The largest copy in each group is kept (green); red copies are in duplicates_list.txt. Click a picture to open it full size.</div></header>
    """;
File.WriteAllText(Path.Combine(dir, "duplicates.html"), html.ToString().Replace("<main>", intro + "<main>"));
File.WriteAllLines(Path.Combine(dir, "duplicates_list.txt"), list);
File.WriteAllLines(Path.Combine(dir, "duplicates.tsv"), table);
File.WriteAllLines(Path.Combine(dir, "duplicate_candidates.tsv"), candidates);

Console.WriteLine($"Groups: {groups.Count} ({exactGroups} exact copies only)   duplicates to remove: {dupCount}   maybe-pairs to judge: {maybePairs.Count}");
Console.WriteLine($"Groups whose kept copy could take an earlier date from a smaller copy: {redates}");
if (unreadable.Count > 0) Console.WriteLine($"Unreadable: {string.Join(", ", unreadable.Take(10))}{(unreadable.Count > 10 ? " ..." : "")}");
Console.WriteLine($"Review: {Path.Combine(dir, "duplicates.html")}");
return 0;

// Largest average difference of any 8x8 block between two grey pictures of the same size.
static double BlockMax(byte[] a, byte[] b, int w, int h)
{
    double worst = 0;
    for (int by = 0; by < h; by += 8)
        for (int bx = 0; bx < w; bx += 8)
        {
            int sum = 0, count = 0;
            for (int y = by; y < Math.Min(by + 8, h); y++)
                for (int x = bx; x < Math.Min(bx + 8, w); x++, count++)
                    sum += Math.Abs(a[y * w + x] - b[y * w + x]);
            worst = Math.Max(worst, (double)sum / count);
        }
    return worst;
}

// Compares two pictures at the smaller one's resolution (at most 480 pixels wide).
static double FineBlockMax(string pathA, string pathB)
{
    using var sa = new MemoryStream(File.ReadAllBytes(pathA));
    using var sb = new MemoryStream(File.ReadAllBytes(pathB));
    using var a = Image.FromStream(sa);
    using var b = Image.FromStream(sb);
    var small = (long)a.Width * a.Height <= (long)b.Width * b.Height ? a : b;
    int w = small.Width, h = small.Height;
    if (w > 480) { h = Math.Max(1, (int)Math.Round(h * 480.0 / w)); w = 480; }
    return BlockMax(Gray(a, w, h), Gray(b, w, h), w, h);
}

static byte[] Gray(Image img, int w, int h)
{
    using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.White);
        g.DrawImage(img, 0, 0, w, h);
    }
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
    var gray = new byte[w * h];
    var row = new byte[data.Stride];
    for (int y = 0; y < h; y++)
    {
        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
        for (int x = 0; x < w; x++)
            gray[y * w + x] = (byte)(0.114 * row[x * 3] + 0.587 * row[x * 3 + 1] + 0.299 * row[x * 3 + 2]);
    }
    bmp.UnlockBits(data);
    return gray;
}

record Pic(string Date, string Source, string File, string Url, int W, int H, long Bytes, string Md5, ulong DHash, byte[] G64, double Spread);
