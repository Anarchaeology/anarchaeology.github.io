// MakeLogo.cs
// Cuts a picture's subject out of its plain light background: the background is flood-filled from the
// edges and made transparent, then the result is cropped to the subject and saved as PNGs for the
// site's logo and browser-tab icon.
//
//   dotnet run MakeLogo.cs -- SOURCE.jpg OUTFOLDER
//
// Writes OUTFOLDER/logo.png (subject at its natural size, padded to a square) and OUTFOLDER/favicon.png (64 px).

#:package System.Drawing.Common@9.0.0
#:property PublishAot=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet run MakeLogo.cs -- SOURCE.jpg OUTFOLDER"); return 2; }
using (new Bitmap(1, 1)) { }   // GDI+ must be started before anything else touches it
using var src = new Bitmap(args[0]);
int w = src.Width, h = src.Height;
var px = new Color[w, h];
for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[x, y] = src.GetPixel(x, y);

// Background = light pixels connected to the border. Anything darker (the subject and its shading) stays.
static bool Light(Color c) => c.R > 215 && c.G > 215 && c.B > 215;
var bg = new bool[w, h];
var stack = new Stack<(int, int)>();
for (int x = 0; x < w; x++) { stack.Push((x, 0)); stack.Push((x, h - 1)); }
for (int y = 0; y < h; y++) { stack.Push((0, y)); stack.Push((w - 1, y)); }
while (stack.Count > 0)
{
    var (x, y) = stack.Pop();
    if (x < 0 || y < 0 || x >= w || y >= h || bg[x, y] || !Light(px[x, y])) continue;
    bg[x, y] = true;
    stack.Push((x + 1, y)); stack.Push((x - 1, y)); stack.Push((x, y + 1)); stack.Push((x, y - 1));
}

int minX = w, minY = h, maxX = -1, maxY = -1;
for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    if (!bg[x, y]) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
int side = Math.Max(maxX - minX + 1, maxY - minY + 1);
int offX = (side - (maxX - minX + 1)) / 2, offY = (side - (maxY - minY + 1)) / 2;

using var logo = new Bitmap(side, side, PixelFormat.Format32bppArgb);
for (int y = minY; y <= maxY; y++)
    for (int x = minX; x <= maxX; x++)
    {
        var c = px[x, y];
        // Soften the cut edge: near-white pixels next to the background get partial transparency.
        bool edge = !bg[x, y] && ((x > 0 && bg[x - 1, y]) || (x < w - 1 && bg[x + 1, y]) || (y > 0 && bg[x, y - 1]) || (y < h - 1 && bg[x, y + 1]));
        int alpha = bg[x, y] ? 0 : edge ? Math.Clamp(255 - (c.R + c.G + c.B) / 3 + 60, 80, 255) : 255;
        logo.SetPixel(x - minX + offX, y - minY + offY, Color.FromArgb(alpha, c));
    }
Directory.CreateDirectory(args[1]);
using (var fs = File.Create(Path.Combine(args[1], "logo.png"))) logo.Save(fs, ImageFormat.Png);
using var icon = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(icon)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(logo, 0, 0, 64, 64); }
using (var fs = File.Create(Path.Combine(args[1], "favicon.png"))) icon.Save(fs, ImageFormat.Png);
Console.WriteLine($"Wrote logo.png ({side}x{side}) and favicon.png (64x64) to {args[1]}");
return 0;
