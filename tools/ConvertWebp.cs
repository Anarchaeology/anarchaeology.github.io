// ConvertWebp.cs
// The other tools read pictures with Windows' built-in image library, which can't open WebP. This converts
// every .webp picture listed in OUTFOLDER/manifest.tsv to a lossless .png next to it, moves the original
// .webp into OUTFOLDER/_webp/ (same subfolders), and updates the manifest's file and size columns.
//
//   dotnet run ConvertWebp.cs -- OUTFOLDER
//
// Afterwards rebuild the contact sheet with: dotnet run FetchWaybackPlan.cs -- PLAN.tsv OUTFOLDER --index-only

#:package SixLabors.ImageSharp@3.2.0

using System.Text;
using SixLabors.ImageSharp;

if (args.Length != 1) { Console.Error.WriteLine("Usage: dotnet run ConvertWebp.cs -- OUTFOLDER"); return 2; }
string outDir = Path.GetFullPath(args[0]);
string manifestPath = Path.Combine(outDir, "manifest.tsv");
var lines = File.ReadAllLines(manifestPath);
int converted = 0;
for (int i = 1; i < lines.Length; i++)
{
    var f = lines[i].Split('\t');
    if (f.Length < 14 || !f[2].EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) continue;
    string src = Path.Combine(outDir, f[2]);
    if (!File.Exists(src)) { Console.WriteLine($"  missing: {f[2]}"); continue; }
    string rel = Path.ChangeExtension(f[2], ".png");
    string dest = Path.Combine(outDir, rel);
    using (var image = Image.Load(src)) image.SaveAsPng(dest);
    File.SetLastWriteTime(dest, File.GetLastWriteTime(src));
    string keep = Path.Combine(outDir, "_webp", f[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
    File.Move(src, keep, overwrite: true);
    (f[2], f[13]) = (rel, new FileInfo(dest).Length.ToString());
    lines[i] = string.Join('\t', f);
    converted++;
}
File.WriteAllLines(manifestPath, lines, new UTF8Encoding(false));
Console.WriteLine($"Converted {converted} WebP pictures to PNG (originals kept in {Path.Combine(outDir, "_webp")})");
return 0;
