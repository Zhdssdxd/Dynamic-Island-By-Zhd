using System;
using System.Windows;
using System.Windows.Media;

namespace WindowsDynamicIsland.UI.Components;

/// <summary>
/// Geometry of the beat crest that grows out of the capsule's bottom edge. <see cref="Outline"/> is the crest itself
/// (filled with the colour of what is behind the island); <see cref="Shade"/> is the black body drawn over it and
/// blurred, so the crest fades from the island's black into that colour along its own contour.
/// Coordinates are local to the strip, whose left edge sits at <see cref="Inset"/> in capsule coordinates.
/// </summary>
internal sealed class CrestWave
{
    public const int Points = 48;
    public const double ShadeBlur = 10;      // blur radius of the black body over the colour
    public const double BandAmplitude = 12;
    private const double Overlap = 1.5;      // strip edge sits this far inside the outline at rest
    private const double ShadeLift = 8;       // the black reaches this far past the crest's edge → no coloured rim/lines
    private const double ColourShare = 0.85;  // share of a tall drop that turns into the colour behind the island
    private const double ShadeMargin = 24;

    private readonly PathFigure _figure = new() { IsClosed = true, IsFilled = true };
    private readonly PolyLineSegment _segment = new();
    private readonly PathFigure _shadeFigure = new() { IsClosed = true, IsFilled = true };
    private readonly PolyLineSegment _shadeSegment = new();

    public CrestWave()
    {
        for (var i = 0; i < Points + 2; i++) _segment.Points.Add(new Point());
        _figure.Segments.Add(_segment);
        Outline = new PathGeometry(new[] { _figure });
        for (var i = 0; i < Points + 4; i++) _shadeSegment.Points.Add(new Point());
        _shadeFigure.Segments.Add(_shadeSegment);
        Shade = new PathGeometry(new[] { _shadeFigure });
    }

    public PathGeometry Outline { get; }
    public PathGeometry Shade { get; }
    public double Inset { get; private set; }
    public double StripWidth { get; private set; }
    public double MaxBulge { get; private set; }

    /// <summary>Rebuilds both geometries in place. Returns false when the capsule is too small to carry a crest.</summary>
    public bool Update(double width, double height, double radius, double beat, double swell, double phase)
    {
        var r = Math.Min(radius, Math.Min(width, height) / 2);
        var inset = r * 0.3;
        var w = width - 2 * inset;
        if (w < 24 || height < 40) return false;
        Inset = inset;
        StripWidth = w;

        var top = height - Math.Max(r, 4);
        _figure.StartPoint = new Point(0, top);
        _segment.Points[0] = new Point(w, top);
        _shadeFigure.StartPoint = new Point(-ShadeMargin, top - ShadeMargin);
        _shadeSegment.Points[0] = new Point(w + ShadeMargin, top - ShadeMargin);

        var centre = 0.5 + 0.1 * Math.Sin(phase * 0.31);
        var side1 = 0.5 + 0.3 * Math.Sin(phase * 0.55 + 1.0);
        var side2 = 0.5 + 0.3 * Math.Sin(phase * 0.55 + 4.1);
        var maxBulge = 0.0;
        for (var j = 0; j <= Points; j++)
        {
            var i = Points - j; // right → left to close the shape
            var u = (double)i / Points;
            var edge = BottomAt(inset + w * u, width, height, r) - Overlap;
            var bulge = Math.Max(0, Bulge(u, beat, swell, centre, side1, side2));
            if (bulge > maxBulge) maxBulge = bulge;
            _segment.Points[j + 1] = new Point(w * u, edge + bulge);
            // Small ripples stay pure black (a 2-px coloured edge on them reads as a stray line); only drops that are
            // tall enough get a coloured tip, growing smoothly with their height.
            var shadeY = edge + bulge + ShadeLift - ColourShare * bulge * SmoothStep(5, 15, bulge);
            _shadeSegment.Points[j + 2] = new Point(w * u, shadeY);
            if (j == 0) _shadeSegment.Points[1] = new Point(w + ShadeMargin, shadeY);
            if (j == Points) _shadeSegment.Points[j + 3] = new Point(-ShadeMargin, shadeY);
        }
        MaxBulge = maxBulge;
        return true;
    }

    /// <summary>
    /// Height of the crest at <paramref name="u"/> (0..1 across the strip): a main drop near the centre driven by the
    /// bass and two smaller drops drifting sideways with the mids, all faded out towards the corners.
    /// </summary>
    internal static double Bulge(double u, double beat, double swell, double centre, double side1, double side2)
    {
        var envelope = Math.Pow(Math.Sin(Math.PI * Math.Clamp(u, 0, 1)), 1.2);
        var main = Gauss(u - centre, 0.13);
        var sides = 0.75 * Gauss(u - side1, 0.09) + 0.6 * Gauss(u - side2, 0.09);
        return envelope * (beat * main + BandAmplitude * swell * sides);
    }

    private static double SmoothStep(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Gauss(double d, double sigma)
    {
        var t = d / sigma;
        return Math.Exp(-t * t);
    }

    /// <summary>Y of the capsule's bottom outline at <paramref name="x"/> (capsule coordinates), honouring the corner radius.</summary>
    internal static double BottomAt(double x, double width, double height, double r)
    {
        if (r <= 0) return height;
        double dx;
        if (x < r) dx = r - x;
        else if (x > width - r) dx = x - (width - r);
        else return height;
        dx = Math.Min(dx, r);
        return height - r + Math.Sqrt(r * r - dx * dx);
    }
}
