// BuildGallery.cs
// Builds the static gallery website (for GitHub Pages) from a FetchWaybackPlan download folder.
//
// Needs the .NET 10 SDK on Windows:
//   dotnet run BuildGallery.cs -- PHOTOFOLDER SITEFOLDER [--link] [--title TEXT] [--repo URL] [--site-url URL] [--template DIR]
//
// PHOTOFOLDER  the download folder (manifest.tsv lists the pictures to include; set-aside ones are not in it)
// SITEFOLDER   where the site is written: index.html, gallery.css, gallery.js, data/photos.json,
//              thumbs/ (small JPEGs for the grid) and photos/ (full-size copies), named as in PHOTOFOLDER
//              except where PHOTOFOLDER\site_names.tsv gives a picture another name (for names ad blockers hide)
// --link       preview only: do not copy full-size photos; the site expects photos/ to be served from
//              PHOTOFOLDER (see serve.js). Nothing is copied into SITEFOLDER except thumbnails.
// --title      site title (default "Anarchy Online Screenshot Archive")
// --repo       the GitHub repository URL, used for the "request a removal" link
// --site-url   the site's public address (e.g. https://name.github.io/), so link previews can show the logo
// --template   folder with index.html, gallery.css and gallery.js (default: gallery-template)
//
// Re-running is incremental: existing thumbnails and copies are kept, and files for pictures no longer in
// the manifest are removed from SITEFOLDER's photos/ and thumbs/.

#:package System.Drawing.Common@9.0.0
#:property NoWarn=CA1416;IL2026;IL3050
#:property PublishAot=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

string? photoDir = null, siteDir = null, repo = null;
string siteUrl = "";
string title = "Anarchy Online Screenshot Archive";
string templateDir = Path.Combine(Environment.CurrentDirectory, "gallery-template");
bool link = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--link") link = true;
    else if (args[i] == "--title" && i + 1 < args.Length) title = args[++i];
    else if (args[i] == "--repo" && i + 1 < args.Length) repo = args[++i].TrimEnd('/');
    else if (args[i] == "--site-url" && i + 1 < args.Length) siteUrl = args[++i].TrimEnd('/') + "/";
    else if (args[i] == "--template" && i + 1 < args.Length) templateDir = args[++i];
    else if (args[i].StartsWith('-')) { Console.Error.WriteLine($"Unknown option: {args[i]}"); return 2; }
    else if (photoDir is null) photoDir = args[i];
    else siteDir = args[i];
}
if (photoDir is null || siteDir is null || !File.Exists(Path.Combine(photoDir, "manifest.tsv")) || !File.Exists(Path.Combine(templateDir, "index.html")))
{
    Console.Error.WriteLine("Usage: dotnet run BuildGallery.cs -- PHOTOFOLDER SITEFOLDER [--link] [--title TEXT] [--repo URL] [--site-url URL] [--template DIR]");
    return 2;
}
photoDir = Path.GetFullPath(photoDir);
siteDir = Path.GetFullPath(siteDir);
if (siteDir.StartsWith(photoDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || siteDir.Equals(photoDir, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("SITEFOLDER must not be inside PHOTOFOLDER.");
    return 2;
}

// ---- Read the curated picture list ----
var rows = new List<string[]>();
using (var reader = new StreamReader(new FileStream(Path.Combine(photoDir, "manifest.tsv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
{
    reader.ReadLine();
    while (reader.ReadLine() is { } line) if (line.Split('\t') is { Length: >= 14 } f) rows.Add(f);
}

// Start GDI+ before asking it for its JPEG encoder (asking first crashes).
using (new Bitmap(1, 1)) { }
var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
using var quality = new EncoderParameters(1);
quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);

var photos = new List<Dictionary<string, object>>();
var keepFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var ids = new HashSet<string>();
var siteRels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// site_names.tsv (optional, in PHOTOFOLDER): a picture's path in the collection <tab> its name on the site.
var siteNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
string siteNamesPath = Path.Combine(photoDir, "site_names.tsv");
if (File.Exists(siteNamesPath))
    foreach (var t in File.ReadLines(siteNamesPath).Where(l => !l.StartsWith('#')).Select(l => l.Split('\t')).Where(t => t.Length >= 2))
        siteNames[t[0].Trim()] = t[1].Trim();
// Words and banner sizes that ad blockers' lists hide; site names matching these are reported after the build.
var adPattern = new Regex(@"(^|[/_.-])(ad|ads|adv|advert|adverts|advertising|banner|banners|bnr|sponsor|sponsors|promo|popup|popunder)([/_.0-9-]|$)" +
                          @"|(^|[^0-9])(468|120|160|728|300|234|125|88|180|250|336)[x_-](60|600|90|250|240|125|31|150|280)([^0-9]|$)", RegexOptions.IgnoreCase);
var adLike = new List<string>();
int copied = 0, thumbsMade = 0, skipped = 0;
foreach (var f in rows)
{
    string rel = f[2].Replace('\\', '/');
    string src = Path.Combine(photoDir, f[2]);
    if (!File.Exists(src)) { skipped++; continue; }

    // Ad blockers hide images whose addresses look like ads (120_600_..., /ads/, banner...), even on this site,
    // so site_names.tsv can give a picture another name on the site. --link previews keep the original path.
    string siteRel = siteNames.GetValueOrDefault(rel, rel);
    string id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(f[5])))[..10].ToLowerInvariant();
    if (!ids.Add(id)) { Console.Error.WriteLine($"Two pictures share the id {id}; the second is {f[5]}"); return 1; }
    if (!siteRels.Add(siteRel)) { Console.Error.WriteLine($"Two pictures would have the site name {siteRel}"); return 1; }
    if (adPattern.IsMatch(siteRel)) adLike.Add(siteRel);
    string fullRel = "photos/" + (link ? rel : siteRel), thumbRel = "thumbs/" + siteRel + ".jpg";
    string thumbPath = Path.Combine(siteDir, thumbRel);
    keepFiles.Add(Path.GetFullPath(thumbPath));

    int w, h;
    try
    {
        using var stream = File.OpenRead(src);
        using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        (w, h) = (img.Width, img.Height);
        if (!File.Exists(thumbPath)) { MakeThumb(img, thumbPath); thumbsMade++; }
    }
    catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException or System.Runtime.InteropServices.ExternalException)
    {
        Console.WriteLine($"  skipped unreadable picture: {rel} ({ex.GetType().Name}: {ex.Message})");
        skipped++;
        continue;
    }

    if (!link)
    {
        string dest = Path.Combine(siteDir, fullRel);
        keepFiles.Add(Path.GetFullPath(dest));
        if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(src).Length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            copied++;
        }
    }

    // Windows ignores case, so a folder first made as "Images" also took the pictures listed under "images";
    // GitHub Pages does not ignore case, so the picture list must spell paths exactly as they are on disk.
    if (!link) fullRel = OnDiskCase(siteDir, fullRel);
    thumbRel = OnDiskCase(siteDir, thumbRel);

    var p = new Dictionary<string, object>
    {
        ["id"] = id,
        ["d"] = f[0], ["ds"] = f[1], ["f"] = fullRel, ["t"] = thumbRel,
        ["n"] = Path.GetFileName(rel),   // the collection's file name (date + original name), used when saving
        ["w"] = w, ["h"] = h, ["b"] = long.Parse(f[13], CultureInfo.InvariantCulture),
        ["s"] = f[4], ["u"] = f[5], ["wb"] = f[6].Replace("id_/", "/"),
        ["cd"] = f[7],
    };
    void Add(string key, string value) { if (value.Length > 0) p[key] = value; }
    Add("sd", f[8]); Add("fd", f[9]); Add("th", f[10]); Add("pd", f[11]); Add("ed", f[12]);
    photos.Add(p);
}
photos = photos.OrderBy(p => (string)p["d"], StringComparer.Ordinal).ThenBy(p => (string)p["f"], StringComparer.OrdinalIgnoreCase).ToList();

// ---- Remove files for pictures that are no longer in the list ----
int removed = 0;
foreach (string sub in new[] { "photos", "thumbs" })
{
    string root = Path.Combine(siteDir, sub);
    if (!Directory.Exists(root)) continue;
    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        if (!keepFiles.Contains(Path.GetFullPath(file))) { File.Delete(file); removed++; }
    // Folders emptied by that (deepest first), e.g. the per-site folders of the old path-based layout.
    foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
}

// ---- Page, data and GitHub Pages files ----
Directory.CreateDirectory(Path.Combine(siteDir, "data"));
File.WriteAllText(Path.Combine(siteDir, "data", "photos.json"), JsonSerializer.Serialize(new Dictionary<string, object>
{
    ["title"] = title,
    ["built"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    ["photos"] = photos,
}));
string contact = repo is null
    ? "To ask for a picture to be removed or credited, open an issue on the GitHub repository that hosts this site."
    : $"To ask for a picture to be removed or credited, <a href=\"{WebUtility.HtmlEncode(repo)}/issues\">open an issue on GitHub</a>.";
File.WriteAllText(Path.Combine(siteDir, "index.html"), File.ReadAllText(Path.Combine(templateDir, "index.html"))
    .Replace("{{TITLE}}", WebUtility.HtmlEncode(title)).Replace("{{CONTACT}}", contact)
    .Replace("{{SITEURL}}", WebUtility.HtmlEncode(siteUrl)));
foreach (string asset in new[] { "gallery.css", "gallery.js", "logo.png", "favicon.png", "robots.txt" })
    if (File.Exists(Path.Combine(templateDir, asset)))
        File.Copy(Path.Combine(templateDir, asset), Path.Combine(siteDir, asset), overwrite: true);
File.WriteAllText(Path.Combine(siteDir, ".nojekyll"), "");   // serve files as-is (folders starting with "_" included)

long siteBytes = Directory.EnumerateFiles(siteDir, "*", SearchOption.AllDirectories)
    .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)).Sum(p => new FileInfo(p).Length);
Console.WriteLine($"{photos.Count} pictures   thumbnails made: {thumbsMade}   full-size copied: {(link ? "none (--link preview)" : copied.ToString())}   " +
                  $"removed: {removed}   skipped: {skipped}");
Console.WriteLine($"Site folder: {siteDir} ({siteBytes / 1048576.0:0} MB)");
if (adLike.Count > 0)
{
    Console.WriteLine($"{adLike.Count} site names look like ads and may be hidden by ad blockers (rename them in site_names.tsv):");
    foreach (string s in adLike) Console.WriteLine("  " + s);
}
return 0;

// The path as spelled on disk, one folder at a time (rel uses "/", is under root, and exists).
static string OnDiskCase(string root, string rel)
{
    string dir = root;
    var parts = new List<string>();
    foreach (string segment in rel.Split('/'))
    {
        string actual = Directory.EnumerateFileSystemEntries(dir, segment).Select(Path.GetFileName).FirstOrDefault() ?? segment;
        parts.Add(actual!);
        dir = Path.Combine(dir, actual!);
    }
    return string.Join('/', parts);
}

void MakeThumb(Image img, string path)
{
    double scale = Math.Min(1.0, Math.Min(400.0 / img.Width, 400.0 / img.Height));
    int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));
    using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.Clear(Color.FromArgb(14, 20, 22));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(img, 0, 0, w, h);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    // Save through a .NET stream: GDI+'s own file saving fails on paths over 260 characters.
    using var file = File.Create(path);
    bmp.Save(file, jpeg, quality);
}
