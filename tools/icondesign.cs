#:package SkiaSharp@2.88.8

// Renders several Windows-11-native design variations of the FreeFlume icon to artifacts/icon-src/
// for review (does NOT touch the app). Usage (repo root): dotnet run tools/icondesign.cs
using SkiaSharp;

static SKColor Rgba(uint rgb, double a) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)(a * 255));

void Draw(SKCanvas canvas, Cfg c)
{
    canvas.Clear(SKColors.Transparent);
    float l = c.Margin, t = c.Margin, rgt = 512 - c.Margin, b = 512 - c.Margin;
    var tile = new SKRect(l, t, rgt, b);
    float r = c.Radius;

    // Soft outer drop shadow (Fluent elevation — the tile floats above the surface).
    if (c.ShadowA > 0)
        using (var sh = new SKPaint { IsAntialias = true, Color = Rgba(0x0e2a47, c.ShadowA) })
        {
            sh.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, c.ShadowBlur);
            canvas.DrawRoundRect(new SKRect(l, t + c.ShadowDy, rgt, b + c.ShadowDy), r, r, sh);
        }

    // Base gradient (gentle, matte).
    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, t), new SKPoint(256, b),
            new[] { SKColor.Parse("#" + c.Top.ToString("x6")), SKColor.Parse("#" + c.Bot.ToString("x6")) },
            null, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(tile, r, r, p);
    }
    // Inner bottom shade for grounding.
    if (c.FloorA > 0)
        using (var p = new SKPaint { IsAntialias = true })
        {
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, t + (b - t) * 0.62f), new SKPoint(256, b),
                new[] { Rgba(0x0a2c4e, 0), Rgba(0x0a2c4e, c.FloorA) }, null, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(tile, r, r, p);
        }
    // Whisper of a top edge-light (matte, not glossy).
    if (c.Sheen > 0)
        using (var p = new SKPaint { IsAntialias = true })
        {
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(256, t), new SKPoint(256, t + (b - t) * 0.30f),
                new[] { Rgba(0xffffff, c.Sheen), Rgba(0xffffff, 0) }, null, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(tile, r, r, p);
        }
    // Hairline inner edge stroke.
    using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Rgba(0xffffff, 0.16) })
        canvas.DrawRoundRect(new SKRect(l + 1, t + 1, rgt - 1, b - 1), r - 1, r - 1, p);

    // Mark is scaled/centered to the tile so padding stays consistent across variations.
    float scale = (rgt - l) / 480f;
    canvas.Save();
    canvas.Translate(256, 256);
    canvas.Scale(scale, scale);
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
        if (c.PlayShadowA > 0) p.ImageFilter = SKImageFilter.CreateDropShadow(0, 7, 8, 8, Rgba(0x0a2c4e, c.PlayShadowA));
        using var path = SKPath.ParseSvgPathData("M 204 156 L 204 312 L 338 234 Z");
        canvas.DrawPath(path, p);
    }
    canvas.Restore();
}

void Save(Cfg c)
{
    using var bmp = new SKBitmap(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp)) Draw(canvas, c);
    // Downscale a touch for the 256 preview.
    using var small = bmp.Resize(new SKImageInfo(256, 256), SKFilterQuality.High);
    using var img = SKImage.FromBitmap(small);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes($"artifacts/icon-src/design-{c.Name}.png", data.ToArray());
}

var variants = new[]
{
    // 1: Floating Fluent tile — matte, soft elevation shadow, faint top sheen.
    new Cfg("1-float", 52, 96, 0x3FA1EA, 0x1F62AE, 0.20, 0.30, 18, 17, 0.16, 0.22),
    // 2: Matte flat — fills more, no sheen, subtle shadow (clean utility tile).
    new Cfg("2-matte", 36, 104, 0x46A3E8, 0x2C70BC, 0.00, 0.20, 12, 12, 0.12, 0.16),
    // 3: Vivid Fluent — brighter azure, a bit more depth.
    new Cfg("3-vivid", 50, 90, 0x2FA0F0, 0x0D5FC4, 0.16, 0.30, 18, 18, 0.18, 0.26),
};
Directory.CreateDirectory("artifacts/icon-src");
foreach (var v in variants) Save(v);
Console.WriteLine("rendered: " + string.Join(", ", variants.Select(v => v.Name)));

record Cfg(
    string Name,
    float Margin,        // tile inset from the 512 canvas (lets it float)
    float Radius,        // corner radius
    uint Top, uint Bot,  // gradient colors
    double Sheen,        // top edge-light opacity (0 = none, matte)
    double ShadowA,      // outer drop-shadow opacity
    float ShadowDy, float ShadowBlur,
    double FloorA,       // inner bottom shade opacity
    double PlayShadowA); // play glyph cast-shadow opacity
