// ReadLiveForum.cs
// Reads thread pages saved from the live Anarchy Online forum (forums.anarchy-online.com, 2001-2004), as fetched
// by HarvestForum.cs, and collects the links in each post with the post's date. No network access.
// Two page layouts: vBulletin 2 (saved 2001-2003) and vBulletin 3 (the forum's upgrade, saved 2004).
//
// Needs the .NET 10 SDK:
//   dotnet run ReadLiveForum.cs -- FOLDER
//
// FOLDER holds pages.tsv (thread id, slug, page, Wayback timestamp, address) and pages/<id>_<page>.html.gz.
// Writes, in FOLDER:
//   links.tsv  thread id, page, post date (yyyy-MM-dd), kind, url, thread title
//              kind: img / link (in the post), sig-img / sig-link (in the poster's signature; these pages were
//              saved while the forum was live, so signatures are the ones used at the time)
//   read.tsv   thread id, page, posts found, links found

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 1) { Console.Error.WriteLine("Usage: dotnet run ReadLiveForum.cs -- FOLDER"); return 2; }
string dir = args[0];
var saved = File.ReadLines(Path.Combine(dir, "pages.tsv")).Select(l => l.Split('\t')).Where(f => f.Length >= 4)
    .ToDictionary(f => f[0] + "_" + f[2], f => f[3]);

// vBulletin 2 (2.0 and 2.2; 2.2 pages have no template comments): each post starts at <a name="postN">; its body
// is the first size-2 paragraph, up to </font></p>; a signature ("__________________") follows before the
// "Report this post" line; the post's date is in the row below.
var anchorRx = new Regex(@"<a name=""post(\d+)""></a>", RegexOptions.Compiled);
const string BodyStart = "<p><font face=\"verdana, arial, helvetica\" size=\"2\" >";
var dateRx = new Regex(@"(January|February|March|April|May|June|July|August|September|October|November|December) (\d{1,2})(?:st|nd|rd|th), (\d{4})|\b(Today|Yesterday)\b", RegexOptions.Compiled);
// vBulletin 3: <!-- post #N --> ... <!-- / post #N -->, the body in <!-- message -->, the signature in <!-- sig -->,
// and the date ("Nov 16th, 2001, 12:11:54") after <!-- status icon and date -->.
var post3Rx = new Regex(@"<!-- post #(\d+) -->([\s\S]*?)<!-- / post #\1 -->", RegexOptions.Compiled);
var body3Rx = new Regex(@"<!-- message -->([\s\S]*?)<!-- / message -->", RegexOptions.Compiled);
var sig3Rx = new Regex(@"<!-- sig -->([\s\S]*?)<!-- / sig -->", RegexOptions.Compiled);
var date3Rx = new Regex(@"<!-- status icon and date -->[\s\S]*?(?:([A-Z][a-z]{2}) (\d{1,2})(?:st|nd|rd|th), (\d{4})|\b(Today|Yesterday)\b)", RegexOptions.Compiled);
var attrRx = new Regex(@"\b(src|href)\s*=\s*[""']?([^""'\s>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
var imageExt = new Regex(@"\.(jpe?g|gif|png|bmp)(\?|#|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

var links = new List<string>();
var read = new List<string>();
int totalPosts = 0;
foreach (string file in Directory.EnumerateFiles(Path.Combine(dir, "pages"), "*.html.gz").Order())
{
    string key = Path.GetFileName(file)[..^".html.gz".Length];   // <id>_<page>
    string[] idPage = key.Split('_');
    using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
    string html = new StreamReader(gz, Encoding.UTF8).ReadToEnd();   // HarvestForum.cs saves pages as UTF-8
    string title = Regex.Match(html, @"<title>\s*([^<]*?)\s*</title>").Groups[1].Value;
    title = WebUtility.HtmlDecode(Regex.Replace(title, @"^Anarchy Online Bulletin Board - ", ""));
    // "Today" and "Yesterday" are relative to the day the Wayback Machine saved the page.
    DateTime savedOn = saved.TryGetValue(key, out string? ts) && ts.Length >= 8
        ? DateTime.ParseExact(ts[..8], "yyyyMMdd", CultureInfo.InvariantCulture) : DateTime.MinValue;
    int posts = 0, before = links.Count;
    var anchors = html.Contains("<!-- post #") ? [] : anchorRx.Matches(html).ToList();
    for (int i = 0; i < anchors.Count; i++)
    {
        int start = anchors[i].Index, stop = i + 1 < anchors.Count ? anchors[i + 1].Index : html.Length;
        string block = html[start..stop];
        posts++;
        string date = PostDate(block, savedOn);
        int b = block.IndexOf(BodyStart, StringComparison.Ordinal);
        if (b < 0) continue;
        int e = block.IndexOf("</font></p>", b, StringComparison.Ordinal);
        if (e < 0) e = block.Length;
        AddLinks(block[b..e], "", date);
        int report = block.IndexOf("<p align=\"right\">", e, StringComparison.Ordinal);
        string rest = block[e..(report >= 0 ? report : block.Length)];
        int rule = rest.IndexOf("__________________", StringComparison.Ordinal);
        if (rule >= 0) AddLinks(rest[rule..], "sig-", date);
    }
    foreach (Match post in post3Rx.Matches(html))
    {
        string block = post.Groups[2].Value;
        posts++;
        var d = date3Rx.Match(block);
        string date = !d.Success ? ""
            : d.Groups[4].Success ? (savedOn == DateTime.MinValue ? "" : (d.Groups[4].Value == "Today" ? savedOn : savedOn.AddDays(-1)).ToString("yyyy-MM-dd"))
            : DateTime.ParseExact($"{d.Groups[1].Value} {d.Groups[2].Value} {d.Groups[3].Value}", "MMM d yyyy", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd");
        if (body3Rx.Match(block) is { Success: true } body) AddLinks(body.Groups[1].Value, "", date);
        if (sig3Rx.Match(block) is { Success: true } sig) AddLinks(sig.Groups[1].Value, "sig-", date);
    }
    totalPosts += posts;
    read.Add(string.Join('\t', idPage[0], idPage[1], posts, links.Count - before));

    void AddLinks(string part, string prefix, string date)
    {
        var seen = new HashSet<string>();
        foreach (Match a in attrRx.Matches(part))
        {
            string url = WebUtility.HtmlDecode(a.Groups[2].Value).Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.Contains("forums.anarchy-online.com", StringComparison.OrdinalIgnoreCase)) continue;
            string kind = prefix + (a.Groups[1].Value.Equals("src", StringComparison.OrdinalIgnoreCase) || imageExt.IsMatch(url) ? "img" : "link");
            if (seen.Add(kind + url)) links.Add(string.Join('\t', idPage[0], idPage[1], date, kind, url, title));
        }
    }
}
File.WriteAllLines(Path.Combine(dir, "links.tsv"), links);
File.WriteAllLines(Path.Combine(dir, "read.tsv"), read);
Console.WriteLine($"{read.Count} pages, {totalPosts} posts, {links.Count} links " +
                  $"({links.Count(l => l.Split('\t')[3] == "img")} pictures in posts, {links.Count(l => l.Split('\t')[3] == "sig-img")} in signatures)");
return 0;

string PostDate(string block, DateTime savedOn)
{
    // The date row comes after the post body; take the last date in the block (the body may mention dates too).
    var m = dateRx.Matches(block).LastOrDefault();
    if (m is null) return "";
    if (m.Groups[4].Success)
        return savedOn == DateTime.MinValue ? "" : (m.Groups[4].Value == "Today" ? savedOn : savedOn.AddDays(-1)).ToString("yyyy-MM-dd");
    return DateTime.ParseExact($"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}", "MMMM d yyyy", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd");
}
