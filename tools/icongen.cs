#:package SkiaSharp@2.88.8

// Draws the FreeFlume app icon — Windows 11 "Float" treatment of the play+flume mark: a matte
// blue tile inset to float on a soft elevation shadow, faint top edge-light, white play with a
// soft cast shadow, flume waves. Writes a multi-resolution .ico plus on-brand square logo PNGs.
// Usage (from repo root): dotnet run tools/icongen.cs
using SkiaSharp;

string icoPath = "src/FreeFlume/Assets/FreeFlume.ico";
string previewPath = "artifacts/icon-src/freeflume-win-256.png";
int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

static SKColor Rgba(uint rgb, double a) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)(a * 255));

static SKBitmap RenderMaster()
{
    const float margin = 52, radius = 96;
    const float l = margin, t = margin, rgt = 512 - margin, b = 512 - margin;

    var bmp = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);
    var tile = new SKRect(l, t, rgt, b);

    // Soft outer drop shadow (Fluent elevation — the tile floats above the surface).
    using (var sh = new SKPaint { IsAntialias = true, Color = Rgba(0x0e2a47, 0.30) })
    {
        sh.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 17);
        canvas.DrawRoundRect(new SKRect(l, t + 18, rgt, b + 18), radius, radius, sh);
    }
    // Base gradient (gentle, matte).
    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, t), new SKPoint(256, b),
            new[] { SKColor.Parse("#3FA1EA"), SKColor.Parse("#1F62AE") }, null, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(tile, radius, radius, p);
    }
    // Inner bottom shade for grounding.
    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, t + (b - t) * 0.62f), new SKPoint(256, b),
            new[] { Rgba(0x0a2c4e, 0), Rgba(0x0a2c4e, 0.16) }, null, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(tile, radius, radius, p);
    }
    // Whisper of a top edge-light (matte, not glossy).
    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, t), new SKPoint(256, t + (b - t) * 0.30f),
            new[] { Rgba(0xffffff, 0.20), Rgba(0xffffff, 0) }, null, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(tile, radius, radius, p);
    }
    // Hairline inner edge stroke.
    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Rgba(0xffffff, 0.16) })
        canvas.DrawRoundRect(new SKRect(l + 1, t + 1, rgt - 1, b - 1), radius - 1, radius - 1, p);

    // Mark scaled/centered to the tile (0.85 keeps the play+waves inside the floated tile).
    canvas.Save();
    canvas.Translate(256, 256);
    canvas.Scale((rgt - l) / 480f, (rgt - l) / 480f);
    canvas.Translate(-256, -256);

    void Wave(string d, double op, float w)
    {
        using var path = SKPath.ParseSvgPathData(d);
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = w, Color = Rgba(0xffffff, op) };
        canvas.DrawPath(path, p);
    }
    Wave("M 154 372 C 192 351 226 351 256 367 C 286 383 320 383 358 361", 0.90, 19);
    Wave("M 170 409 C 203 392 232 392 256 404 C 280 416 309 416 342 399", 0.42, 13);

    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = 28, Color = SKColors.White })
    {
        p.ImageFilter = SKImageFilter.CreateDropShadow(0, 7, 8, 8, Rgba(0x0a2c4e, 0.22));
        using var path = SKPath.ParseSvgPathData("M 204 156 L 204 312 L 338 234 Z");
        canvas.DrawPath(path, p);
    }
    canvas.Restore();
    canvas.Flush();
    return bmp;
}

static byte[] EncodePng(SKBitmap master, int size)
{
    using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(SKColors.Transparent);
        float s = size / 512f;
        canvas.Scale(s, s);
        using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(master, 0, 0, p);
    }
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

using var master = RenderMaster();

var pngs = sizes.Select(s => EncodePng(master, s)).ToArray();
using (var fs = File.Create(icoPath))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write((uint)pngs[i].Length); w.Write((uint)offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs) w.Write(png);
}

Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
File.WriteAllBytes(previewPath, EncodePng(master, 256));

(string path, int size)[] logos =
{
    ("src/FreeFlume/Assets/Square44x44Logo.scale-200.png", 88),
    ("src/FreeFlume/Assets/Square44x44Logo.targetsize-24_altform-unplated.png", 24),
    ("src/FreeFlume/Assets/Square150x150Logo.scale-200.png", 300),
    ("src/FreeFlume/Assets/StoreLogo.png", 50),
    ("src/FreeFlume/Assets/LockScreenLogo.scale-200.png", 48),
};
foreach (var (path, size) in logos) File.WriteAllBytes(path, EncodePng(master, size));

Console.WriteLine($"wrote {icoPath} ({new FileInfo(icoPath).Length} bytes), preview + {logos.Length} logos");
return 0;
