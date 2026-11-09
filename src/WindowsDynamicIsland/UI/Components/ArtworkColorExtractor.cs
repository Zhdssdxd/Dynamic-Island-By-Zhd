using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>
/// Picks a vivid accent color from album art (used when ColorScheme == Adaptive to tint the visualizer and
/// progress). Samples a ~24 px downscaled copy, buckets pixels by hue weighted by saturation × brightness,
/// averages the winning bucket and lifts it so it stays readable on a black capsule.
/// Results are cached per image instance (weakly — the cache never keeps artwork alive).
/// </summary>
public static class ArtworkColorExtractor
{
    private const int SampleSize = 24;
    private const int HueBuckets = 12;
    private static readonly ConditionalWeakTable<ImageSource, StrongBox<Color?>> Cache = new();

    /// <summary>Accent color, or null when the image is (nearly) grayscale / unreadable → use white.</summary>
    public static Color? GetAccentColor(ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return null;
        if (Cache.TryGetValue(bitmap, out var cached)) return cached.Value;

        Color? result = null;
        try
        {
            result = Compute(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn("Artwork color extraction failed", ex);
        }
        Cache.AddOrUpdate(bitmap, new StrongBox<Color?>(result));
        return result;
    }

    private const int BlurSize = 5;
    private static readonly ConditionalWeakTable<ImageSource, StrongBox<Brush?>> BlurCache = new();

    /// <summary>
    /// The album art squeezed to 5×5 pixels, colours lifted (more saturation, at least 75 % brightness) and stretched
    /// back with linear filtering — an extremely blurred, vivid copy of the cover for painting the sound wave.
    /// Null when there is no readable artwork. Cached weakly per image.
    /// </summary>
    public static Brush? GetBlurredBrush(ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return null;
        if (BlurCache.TryGetValue(bitmap, out var cached)) return cached.Value;

        Brush? result = null;
        try
        {
            result = ComputeBlurredBrush(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn("Artwork blur failed", ex);
        }
        BlurCache.AddOrUpdate(bitmap, new StrongBox<Brush?>(result));
        return result;
    }

    private static Brush? ComputeBlurredBrush(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;

        // Two-step downscale (≈24 px, then 5 px) averages the whole cover instead of point-sampling it.
        BitmapSource src = bitmap;
        var s1 = Math.Min(1.0, 24.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        if (s1 < 1.0) src = new TransformedBitmap(src, new ScaleTransform(s1, s1));
        src = new TransformedBitmap(src, new ScaleTransform((double)BlurSize / src.PixelWidth, (double)BlurSize / src.PixelHeight));
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        var w = src.PixelWidth;
        var h = src.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        src.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
            RgbToHsv(r, g, b, out var hue, out var sat, out var val);
            sat = Math.Clamp(sat * 1.35, 0, 0.9);
            val = Math.Clamp(Math.Max(val * 1.15, 0.75), 0, 1);
            HsvToRgb(hue, sat, val, out r, out g, out b);
            pixels[i] = ToByte(b);
            pixels[i + 1] = ToByte(g);
            pixels[i + 2] = ToByte(r);
            pixels[i + 3] = 255;
        }

        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        brush.Freeze();
        return brush;
    }

    private static Color? Compute(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return null;

        BitmapSource src = bitmap;
        var sx = Math.Min(1.0, (double)SampleSize / bitmap.PixelWidth);
        var sy = Math.Min(1.0, (double)SampleSize / bitmap.PixelHeight);
        if (sx < 1.0 || sy < 1.0) src = new TransformedBitmap(src, new ScaleTransform(sx, sy));
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        var w = src.PixelWidth;
        var h = src.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        src.CopyPixels(pixels, stride, 0);

        var weight = new double[HueBuckets];
        var sumR = new double[HueBuckets];
        var sumG = new double[HueBuckets];
        var sumB = new double[HueBuckets];
        var counted = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a < 128) continue;
            double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
            counted++;
            RgbToHsv(r, g, b, out var hue, out var sat, out var val);
            if (sat < 0.22 || val < 0.18) continue;
            var wgt = sat * sat * val;
            var bucket = Math.Min(HueBuckets - 1, (int)(hue / 360.0 * HueBuckets));
            weight[bucket] += wgt;
            sumR[bucket] += r * wgt;
            sumG[bucket] += g * wgt;
            sumB[bucket] += b * wgt;
        }

        if (counted == 0) return null;
        var best = 0;
        for (var k = 1; k < HueBuckets; k++)
            if (weight[k] > weight[best]) best = k;

        // Not colorful enough (grayscale art, mostly dark) → no tint.
        if (weight[best] < counted * 0.02) return null;

        var rr = sumR[best] / weight[best];
        var gg = sumG[best] / weight[best];
        var bb = sumB[best] / weight[best];
        RgbToHsv(rr, gg, bb, out var h2, out var s2, out var v2);
        s2 = Math.Clamp(s2, 0.35, 0.80);
        v2 = Math.Max(v2, 0.88);
        HsvToRgb(h2, s2, v2, out rr, out gg, out bb);
        return Color.FromRgb(ToByte(rr), ToByte(gg), ToByte(bb));
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 1e-9)
        {
            h = 0;
            return;
        }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;
        if (h >= 360) h -= 360;
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;
        (r, g, b) = (h / 60.0) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        r += m;
        g += m;
        b += m;
    }
}
