// MiaLevels.cs
// Helps read the character level shown in Anarchy Online screenshots (the Stats panel on the right of the
// interface: name, then "LEVEL nn" below it), so screenshots can be ordered by level.
//
// Needs the .NET 10 SDK on Windows:
//   dotnet run MiaLevels.cs -- zoom SRC X Y W H SCALE OUT.png        enlarge a region with a 10-pixel grid
//   dotnet run MiaLevels.cs -- cut  SRC X Y W H OUT.png              save a region as a template
//   dotnet run MiaLevels.cs -- find FOLDER TEMPLATE.png OUTDIR [--min SCORE] [--band FAR NEAR] [--crop DX DY W H] [--scale N]
//        For every picture in FOLDER, looks for TEMPLATE (the "LEVEL" label) in the right-hand column of the
//        screen, at any height. Writes OUTDIR\found.tsv (file, score, x, y) and contact sheets
//        OUTDIR\sheet_NN.png showing the name and level rows of each match, numbered to match found.tsv.

#:package System.Drawing.Common@9.0.0
#:property NoWarn=CA1416
#:property PublishAot=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

var inv = CultureInfo.InvariantCulture;
if (args.Length == 0) return Usage();
switch (args[0])
{
    case "zoom" when args.Length == 8:
    {
        using var img = Load(args[1]);
        var r = new Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
        int s = int.Parse(args[6]);
        using var bmp = new Bitmap(r.Width * s, r.Height * s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(img, new Rectangle(0, 0, bmp.Width, bmp.Height), r, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.FromArgb(110, 255, 0, 0));
            for (int x = 10 - r.X % 10; x < r.Width; x += 10) g.DrawLine(pen, x * s, 0, x * s, bmp.Height);
            for (int y = 10 - r.Y % 10; y < r.Height; y += 10) g.DrawLine(pen, 0, y * s, bmp.Width, y * s);
        }
        Save(bmp, args[7]);
        return 0;
    }
    case "cut" when args.Length == 7:
    {
        using var img = Load(args[1]);
        var r = new Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        Save(bmp, args[6]);
        return 0;
    }
    case "find" when args.Length >= 4:
    {
        double minScore = 0.75;
        int bandFar = 240, bandNear = 120;               // search x between these distances from the right edge
        var cropBox = new Rectangle(-8, -22, 192, 34);   // name and level rows, relative to the label
        int scale = 2;
        for (int i = 4; i < args.Length; i++)
        {
            if (args[i] == "--min") minScore = double.Parse(args[++i], inv);
            else if (args[i] == "--band") { bandFar = int.Parse(args[++i]); bandNear = int.Parse(args[++i]); }
            else if (args[i] == "--crop") cropBox = new Rectangle(int.Parse(args[++i]), int.Parse(args[++i]), int.Parse(args[++i]), int.Parse(args[++i]));
            else if (args[i] == "--scale") scale = int.Parse(args[++i]);
        }
        string outDir = args[3];
        Directory.CreateDirectory(outDir);
        float[] tpl;
        int tw, th;
        using (var t = Load(args[2])) { (tw, th) = (t.Width, t.Height); tpl = Gray((Bitmap)t); }
        double tMean = tpl.Average(), tNorm = Math.Sqrt(tpl.Sum(v => (v - tMean) * (v - tMean)));

        var files = Directory.EnumerateFiles(args[1]).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<string> { "n\tfile\tscore\tx\ty\twidth\theight" };
        var crops = new List<(int N, Bitmap Crop)>();
        int n = 0;
        foreach (string file in files)
        {
            using var img = Load(file);
            using var bmp = new Bitmap(img);
            int w = bmp.Width, h = bmp.Height;
            var gray = Gray(bmp);
            // The Stats panel sits against the right edge; search a band of x positions near it, at any height.
            int x0 = Math.Max(0, w - bandFar), x1 = Math.Max(0, w - bandNear - tw);
            double best = -1; int bx = 0, by = 0;
            for (int y = 0; y + th <= h; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double s = Ncc(gray, w, x, y, tpl, tw, th, tMean, tNorm);
                    if (s > best) { best = s; bx = x; by = y; }
                }
            string name = Path.GetFileName(file);
            if (best >= minScore)
            {
                n++;
                rows.Add(string.Join('\t', n, name, best.ToString("0.000", inv), bx, by, w, h));
                // The name row sits above the label; the crop box keeps both rows across the panel.
                var r = Rectangle.Intersect(new Rectangle(bx + cropBox.X, by + cropBox.Y, cropBox.Width, cropBox.Height), new Rectangle(0, 0, w, h));
                var crop = new Bitmap(r.Width, r.Height);
                using (var g = Graphics.FromImage(crop)) g.DrawImage(bmp, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                crops.Add((n, crop));
            }
            else rows.Add(string.Join('\t', "-", name, best.ToString("0.000", inv), "", "", w, h));
        }
        File.WriteAllLines(Path.Combine(outDir, "found.tsv"), rows);

        // Contact sheets: enlarged crops, numbered to match found.tsv, about 1400 pixels wide.
        int cellW = cropBox.Width * scale + 70, cellH = cropBox.Height * scale + 8;
        int cols = Math.Max(1, 1400 / cellW), perSheet = cols * Math.Max(1, 1150 / cellH);
        using var font = new Font("Consolas", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        for (int s = 0; s * perSheet < crops.Count; s++)
        {
            var page = crops.Skip(s * perSheet).Take(perSheet).ToList();
            int rowsOnSheet = (page.Count + cols - 1) / cols;
            using var sheet = new Bitmap(cols * cellW, rowsOnSheet * cellH);
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.White);
                g.InterpolationMode = scale > 3 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = 0; i < page.Count; i++)
                {
                    int cx = i % cols * cellW, cy = i / cols * cellH;
                    g.DrawString("#" + page[i].N, font, Brushes.Black, cx + 2, cy + 22);
                    g.DrawImage(page[i].Crop, new Rectangle(cx + 66, cy + 4, page[i].Crop.Width * scale, page[i].Crop.Height * scale));
                }
            }
            Save(sheet, Path.Combine(outDir, $"sheet_{s + 1:00}.png"));
        }
        foreach (var c in crops) c.Crop.Dispose();
        Console.WriteLine($"{files.Count} pictures, {crops.Count} with the label (score >= {minScore}), {(crops.Count + perSheet - 1) / perSheet} sheets in {outDir}");
        return 0;
    }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("Usage: dotnet run MiaLevels.cs -- zoom SRC X Y W H SCALE OUT.png | cut SRC X Y W H OUT.png | find FOLDER TEMPLATE.png OUTDIR [--min SCORE]");
    return 2;
}

// Reads through a .NET stream (GDI+'s own file access fails on long paths).
static Image Load(string path) => Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));

static void Save(Bitmap bmp, string path)
{
    using var file = File.Create(path);
    bmp.Save(file, ImageFormat.Png);
}

static float[] Gray(Bitmap src)
{
    using var bmp = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb);
    var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
    var gray = new float[bmp.Width * bmp.Height];
    var row = new byte[data.Stride];
    for (int y = 0; y < bmp.Height; y++)
    {
        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
        for (int x = 0; x < bmp.Width; x++)
            gray[y * bmp.Width + x] = 0.114f * row[x * 3] + 0.587f * row[x * 3 + 1] + 0.299f * row[x * 3 + 2];
    }
    bmp.UnlockBits(data);
    return gray;
}

// Normalized cross-correlation of the template with the picture at (x, y): 1 = identical pattern.
static double Ncc(float[] img, int w, int x, int y, float[] t, int tw, int th, double tMean, double tNorm)
{
    double sum = 0;
    for (int j = 0; j < th; j++) { int o = (y + j) * w + x; for (int i = 0; i < tw; i++) sum += img[o + i]; }
    double mean = sum / (tw * th), num = 0, den = 0;
    for (int j = 0; j < th; j++)
    {
        int o = (y + j) * w + x;
        for (int i = 0; i < tw; i++)
        {
            double d = img[o + i] - mean;
            num += d * (t[j * tw + i] - tMean);
            den += d * d;
        }
    }
    return den <= 0 || tNorm <= 0 ? 0 : num / (Math.Sqrt(den) * tNorm);
}
