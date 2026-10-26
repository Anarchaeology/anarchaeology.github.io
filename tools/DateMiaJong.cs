// DateMiaJong.cs
// Proposes dates for MiaJong's screenshots (perso.wanadoo.fr/fmpthemad/ao) from the character level shown in
// them, her level history on auno.org, and her file numbering (ao_1 ... ao_1115 were saved in order).
//
//   dotnet run DateMiaJong.cs -- PHOTOFOLDER AUNO_LEVELS.tsv LEVELS.tsv OUT.tsv
//
// AUNO_LEVELS.tsv: date <tab> level, from auno.org's character history.
// LEVELS.tsv:      file name without date prefix (e.g. ao_70.jpg) <tab> level read from the screenshot.
//
// How a date is chosen:
//   level  - the screenshot shows her level L: the date falls while she was level L (between reaching L and
//            reaching L+1, interpolated from auno.org); screenshots at the same level are spread across that
//            span in file-number order. Never later than a trustworthy upload date of the same file.
//   order  - no level shown: placed by file number between the nearest dated screenshots before and after.
//   server - trustworthy upload dates (not the bulk re-uploads) are kept and also used as fixed points.
//   series - files outside the numbered series (reet1.jpg, paysage_4.jpg) re-uploaded in bulk take the upload
//            date of the other files in their series. Milestone sets named with her level (mia160_3.jpg)
//            are dated by that level.
// OUT.tsv lists every file in the folder with its old and proposed date; nothing is changed on disk.

using System.Globalization;
using System.Text.RegularExpressions;

if (args.Length != 4) { Console.Error.WriteLine("Usage: dotnet run DateMiaJong.cs -- PHOTOFOLDER AUNO_LEVELS.tsv LEVELS.tsv OUT.tsv"); return 2; }
var inv = CultureInfo.InvariantCulture;
DateTime Day(string s) => DateTime.ParseExact(s, "yyyy-MM-dd", inv);
string Fmt(DateTime d) => d.ToString("yyyy-MM-dd", inv);
// The 2005-01-10 re-upload covers most of the folder. Other upload dates are trusted only where they agree
// with the level dates around them (a few files re-uploaded on 2003-02-05 are thrown out that way).
var bulkUploads = new HashSet<string> { "2005-01-10" };

var auno = File.ReadLines(args[1]).Select(l => l.Split('\t')).Where(f => f.Length >= 2)
    .Select(f => (Date: Day(f[0]), Level: int.Parse(f[1], inv))).OrderBy(p => p.Date).ToList();
var levels = File.ReadLines(args[2]).Select(l => l.Split('\t')).Where(f => f.Length >= 2)
    .ToDictionary(f => f[0], f => int.Parse(f[1], inv), StringComparer.OrdinalIgnoreCase);

// When she reached level L: linear interpolation between consecutive auno.org observations.
DateTime Reach(double level)
{
    if (level <= auno[0].Level) return auno[0].Date;
    for (int i = 1; i < auno.Count; i++)
    {
        var (d0, l0) = auno[i - 1];
        var (d1, l1) = auno[i];
        if (l0 < level && level <= l1) return d0 + (d1 - d0) * ((level - l0) / (double)(l1 - l0));
    }
    return auno[^1].Date;
}

// ---- Her files, from the manifest ----
var files = new List<Pic>();
using (var reader = new StreamReader(new FileStream(Path.Combine(args[0], "manifest.tsv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
{
    reader.ReadLine();
    while (reader.ReadLine() is { } line)
    {
        var f = line.Split('\t');
        if (f.Length < 14 || !f[2].Replace('\\', '/').Contains("perso.wanadoo.fr/fmpthemad/ao/", StringComparison.OrdinalIgnoreCase)) continue;
        string name = Regex.Replace(Path.GetFileName(f[2]), @"^\d{4}-\d{2}-\d{2}_", "");
        var m = Regex.Match(name, @"^ao_(\d+)\.", RegexOptions.IgnoreCase);
        // Milestone sets carry her level in the name: mia160_3.jpg, mia170_2.jpg, mia_200_5.jpg.
        var named = Regex.Match(name, @"^mia_?(\d{3})_\d+\.", RegexOptions.IgnoreCase);
        int? level = levels.TryGetValue(name, out int lv) ? lv : named.Success ? int.Parse(named.Groups[1].Value, inv) : null;
        files.Add(new Pic(f[2], f[5], name, m.Success ? int.Parse(m.Groups[1].Value, inv) : (int?)null, level, f[0], f[1], f[8],
                          Milestone: named.Success && !levels.ContainsKey(name)));
    }
}

var proposed = new Dictionary<Pic, (DateTime Date, string Source)>();

// ---- 1. Screenshots that show her level ----
// Milestone sets (named for a level) were most likely taken on reaching it.
foreach (var p in files.Where(p => p.Milestone)) proposed[p] = (Reach(p.Level!.Value).Date, "level");
foreach (var group in files.Where(p => p.Level is not null && !p.Milestone).GroupBy(p => p.Level!.Value))
{
    var list = group.OrderBy(p => p.Number ?? int.MaxValue).ThenBy(p => p.Name).ToList();
    DateTime from = Reach(group.Key), to = Reach(group.Key + 1);
    for (int i = 0; i < list.Count; i++)
    {
        var d = (from + (to - from) * ((i + 0.5) / list.Count)).Date;
        var p = list[i];
        if (p.Server.Length > 0 && !bulkUploads.Contains(p.Server) && Day(p.Server) < d) d = Day(p.Server);  // uploaded by then
        proposed[p] = (d, "level");
    }
}

// ---- 2. Fixed points along the file numbers: level dates, plus trustworthy upload dates that agree with them ----
var anchors = proposed.Where(kv => kv.Key.Number is not null).Select(kv => (N: kv.Key.Number!.Value, kv.Value.Date)).ToList();
var levelAnchors = anchors.OrderBy(a => a.N).ToList();
foreach (var p in files.Where(p => p.Number is not null && p.Level is null && p.Server.Length > 0 && !bulkUploads.Contains(p.Server)))
{
    var d = Day(p.Server);
    var before = levelAnchors.LastOrDefault(a => a.N < p.Number);
    var after = levelAnchors.FirstOrDefault(a => a.N > p.Number);
    bool fits = (before == default || d >= before.Date.AddDays(-3)) && (after == default || d <= after.Date.AddDays(14));
    if (!fits) continue;
    anchors.Add((p.Number!.Value, d));
    proposed[p] = (d, p.CurrentSource);   // keep as it is
}
anchors = anchors.OrderBy(a => a.N).ThenBy(a => a.Date).ToList();

// ---- 3. Everything else in the numbered series: between the nearest fixed points ----
foreach (var p in files.Where(p => p.Number is not null && !proposed.ContainsKey(p)))
{
    int n = p.Number!.Value;
    var before = anchors.LastOrDefault(a => a.N < n);
    var after = anchors.FirstOrDefault(a => a.N > n);
    DateTime d;
    if (before == default && after == default) continue;
    if (before == default) d = after.Date;
    else if (after == default) d = before.Date;
    else d = before.Date + (after.Date - before.Date) * ((n - before.N) / (double)(after.N - before.N));
    if (p.Server.Length > 0 && !bulkUploads.Contains(p.Server) && Day(p.Server) < d) d = Day(p.Server);
    proposed[p] = (d.Date, "order");
}

// ---- 3b. Other named series (reet1.jpg, paysage_4.jpg ...): take the upload date the rest of the series has ----
string Series(string name) => Regex.Replace(Path.GetFileNameWithoutExtension(name), @"\d+(big|small)?$", "", RegexOptions.IgnoreCase).ToLowerInvariant();
foreach (var group in files.Where(p => p.Number is null).GroupBy(p => Series(p.Name)))
{
    var dated = group.Where(p => p.Server.Length > 0 && !bulkUploads.Contains(p.Server)).Select(p => Day(p.Server)).ToList();
    if (dated.Count == 0) continue;
    var seriesDate = dated.Max();
    foreach (var p in group.Where(p => !proposed.ContainsKey(p) && (p.Server.Length == 0 || bulkUploads.Contains(p.Server))))
        proposed[p] = (seriesDate, "series");
}

// ---- 4. Files are numbered in the order taken, so a file uploaded on day X means every lower-numbered file
// was taken by day X too. Apply that ceiling from each upload date to everything numbered before it. ----
DateTime ceiling = DateTime.MaxValue;
foreach (var p in files.Where(p => p.Number is not null).OrderByDescending(p => p.Number))
{
    if (p.Server.Length > 0 && Day(p.Server) < ceiling) ceiling = Day(p.Server);
    if (proposed.TryGetValue(p, out var v) && v.Date > ceiling) proposed[p] = (ceiling, v.Source);
}

// ---- Output ----
var rows = new List<string> { "file\turl\tnumber\tlevel\told_date\told_source\tnew_date\tnew_source\tdays_moved" };
foreach (var p in files.OrderBy(p => p.Number ?? int.MaxValue).ThenBy(p => p.Name))
{
    var (date, source) = proposed.TryGetValue(p, out var v) ? v : (Day(p.Current), p.CurrentSource);
    int moved = (int)(date - Day(p.Current)).TotalDays;
    rows.Add(string.Join('\t', p.File, p.Url, p.Number?.ToString(inv) ?? "", p.Level?.ToString(inv) ?? "",
                         p.Current, p.CurrentSource, Fmt(date), source, moved.ToString(inv)));
}
File.WriteAllLines(args[3], rows);

var changed = files.Where(p => proposed.TryGetValue(p, out var v) && Fmt(v.Date) != p.Current).ToList();
Console.WriteLine($"{files.Count} files: {proposed.Count(kv => kv.Value.Source == "level")} dated by level, " +
                  $"{proposed.Count(kv => kv.Value.Source == "order")} by file order, {anchors.Count - levelAnchors.Count} upload dates kept as fixed points, " +
                  $"{files.Count - proposed.Count} unchanged (not in the numbered series and no level). {changed.Count} dates would change.");
return 0;

record Pic(string File, string Url, string Name, int? Number, int? Level, string Current, string CurrentSource, string Server, bool Milestone = false);
