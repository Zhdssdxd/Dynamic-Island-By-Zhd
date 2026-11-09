using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WindowsDynamicIsland.UI;
using WindowsDynamicIsland.UI.Components;
using Xunit;

namespace WindowsDynamicIsland.Tests;

public class CrestRenderTests
{
    [Fact]
    public void Crest_StaysInsideTheStrip_AndRestsOnTheOutline()
    {
        var crest = new CrestWave();
        Assert.True(crest.Update(400, 164, 30, 0, 0, 0));
        // At rest the crest's lower edge never goes below the capsule outline.
        var b = crest.Outline.Bounds;
        Assert.True(b.Bottom <= 164 + 0.01, $"bottom {b.Bottom}");
        Assert.True(b.Left >= -0.01 && b.Right <= crest.StripWidth + 0.01);
        Assert.True(crest.Update(400, 164, 30, 18, 1, 2));
        Assert.True(crest.Outline.Bounds.Bottom > 170);
    }

    /// <summary>
    /// Renders the real WPF visual (capsule + crest + shade) to PNGs when WAVE_RENDER_DIR is set, for eyeballing.
    /// </summary>
    [Fact]
    public void RenderCrestFrames()
    {
        var dir = Environment.GetEnvironmentVariable("WAVE_RENDER_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var cases = new (string Name, double Beat, double Swell, double Phase, Color Left, Color Right)[]
                {
                    ("rest", 0, 0, 0, Color.FromRgb(176, 214, 226), Color.FromRgb(176, 214, 226)),
                    ("mids", 2, 0.7, 1.0, Color.FromRgb(90, 160, 240), Color.FromRgb(250, 170, 90)),
                    ("kick", 18, 0.9, 2.2, Color.FromRgb(70, 40, 110), Color.FromRgb(200, 60, 120)),
                    ("kick2", 14, 0.8, 4.5, Color.FromRgb(230, 230, 230), Color.FromRgb(40, 160, 90)),
                    ("dark", 12, 0.6, 3.3, Color.FromRgb(20, 22, 28), Color.FromRgb(30, 30, 40)),
                };
                foreach (var c in cases) Render(Path.Combine(dir, c.Name + ".png"), c.Beat, c.Swell, c.Phase, c.Left, c.Right, 3);
                var k = cases[3];
                Render(Path.Combine(dir, "kick2_1x.png"), k.Beat, k.Swell, k.Phase, k.Left, k.Right, 1);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static void Render(string file, double beat, double swell, double phase, Color left, Color right, int scale)
    {
        const double W = 400, H = 164, R = 30, ox = 40, oy = 10;
        var root = new Grid
        {
            Width = W + 2 * ox,
            Height = H + oy + 70,
            Background = new LinearGradientBrush(left, right, 0),
        };
        var host = new Grid { Width = W, Height = H, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(ox, oy, 0, 0) };
        host.Children.Add(new Border
        {
            Background = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(R),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        });

        var crest = new CrestWave();
        crest.Update(W, H, R, beat, swell, phase);
        var colour = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(crest.StripWidth, 0) };
        colour.GradientStops.Add(new GradientStop(IslandWindow.Vivid(left), 0));
        colour.GradientStops.Add(new GradientStop(IslandWindow.Vivid(right), 1));
        var group = new Canvas { Clip = crest.Outline };
        group.Children.Add(new System.Windows.Shapes.Path { Data = crest.Outline, Fill = colour });
        group.Children.Add(new System.Windows.Shapes.Path { Data = crest.Shade, Fill = Brushes.Black, Effect = new BlurEffect { Radius = CrestWave.ShadeBlur, KernelType = KernelType.Gaussian } });
        Canvas.SetLeft(group, crest.Inset);
        var layer = new Canvas { ClipToBounds = false, IsHitTestVisible = false };
        layer.Children.Add(group);
        host.Children.Add(layer);
        root.Children.Add(host);

        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)(root.Width * scale), (int)(root.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
