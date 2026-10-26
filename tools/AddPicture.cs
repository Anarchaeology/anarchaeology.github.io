// AddPicture.cs
// Adds one reviewed picture straight to the built site, for when the collection folder isn't at hand: copies the
// picture into photos/, makes its thumbnail the way BuildGallery.cs does, and adds its record to data/photos.json
// (same fields, same order, same sorting).
//
// Needs the .NET 10 SDK:
//   dotnet run AddPicture.cs -- REVIEWFOLDER "site\folders\date_name.jpg" SITEFOLDER
//
// REVIEWFOLDER is a folder made by FetchWaybackPlan.cs (the picture plus its row in manifest.tsv); the second argument
// is the picture's path in it, as manifest.tsv lists it.

#:package System.Drawing.Common@9.0.0
#:property NoWarn=CA1416;IL2026;IL3050
#:property PublishAot=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 3) { Console.Error.WriteLine("Usage: dotnet run AddPicture.cs -- REVIEWFOLDER PICTUREPATH SITEFOLDER"); return 2; }
string reviewDir = args[0], rel = args[1].Replace('/', '\\'), siteDir = args[2];
var f = File.ReadLines(Path.Combine(reviewDir, "manifest.tsv")).Skip(1).Select(l => l.Split('\t'))
    .FirstOrDefault(r => r.Length >= 14 && r[2].Equals(rel, StringComparison.OrdinalIgnoreCase));
if (f is null) { Console.Error.WriteLine($"{rel} isn't in {reviewDir}\\manifest.tsv"); return 1; }

string relWeb = f[2].Replace('\\', '/');
string id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(f[5])))[..10].ToLowerInvariant();
string photoPath = Path.Combine(siteDir, "photos", relWeb), thumbPath = Path.Combine(siteDir, "thumbs", relWeb + ".jpg");
string jsonPath = Path.Combine(siteDir, "data", "photos.json");
var root = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
var photos = root["photos"]!.AsArray();
if (photos.Any(p => (string?)p!["id"] == id)) { Console.Error.WriteLine($"{f[5]} is already in the site (id {id})"); return 1; }

Directory.CreateDirectory(Path.GetDirectoryName(photoPath)!);
File.Copy(Path.Combine(reviewDir, f[2]), photoPath, overwrite: false);
int w, h;
using (var stream = File.OpenRead(photoPath))
using (var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false))
{
    (w, h) = (img.Width, img.Height);
    MakeThumb(img, thumbPath);
}

var p = new JsonObject
{
    ["id"] = id, ["d"] = f[0], ["ds"] = f[1], ["f"] = "photos/" + relWeb, ["t"] = "thumbs/" + relWeb + ".jpg",
    ["n"] = Path.GetFileName(relWeb), ["w"] = w, ["h"] = h, ["b"] = long.Parse(f[13], CultureInfo.InvariantCulture),
    ["s"] = f[4], ["u"] = f[5], ["wb"] = f[6].Replace("id_/", "/"), ["cd"] = f[7],
};
foreach (var (key, col) in new[] { ("sd", 8), ("fd", 9), ("pd", 11), ("ed", 12) })
    if (f[col].Length > 0) p[key] = f[col];
// "ft": where it was posted on Funcom's forum, as a page of the forum archive (N.html, or N-2.html#post123 when set
// by hand); "th" is a JeuxOnLine thread
if (f[10].Length > 0) p["ft"] = f[10] + ".html";
var sorted = photos.Select(x => x!.DeepClone()).Append(p)
    .OrderBy(x => (string)x!["d"]!, StringComparer.Ordinal).ThenBy(x => (string)x!["f"]!, StringComparer.OrdinalIgnoreCase).ToList();
root["photos"] = new JsonArray(sorted.ToArray());
root["built"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
File.WriteAllText(jsonPath, root.ToJsonString());
Console.WriteLine($"added {relWeb} ({w}x{h}, {f[0]} {f[1]}) as #{id}; the site now has {sorted.Count} pictures");
return 0;

static void MakeThumb(Image img, string path)
{
    using (new Bitmap(1, 1)) { }
    var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    using var quality = new EncoderParameters(1);
    quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
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
    using var file = File.Create(path);
    bmp.Save(file, jpeg, quality);
}
