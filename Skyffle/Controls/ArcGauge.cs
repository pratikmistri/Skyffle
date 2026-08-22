using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
// System.IO.Path arrives via ImplicitUsings and would otherwise shadow the shape
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Skyffle.Controls;

/// <summary>One coloured band of a gauge face.</summary>
public sealed class GaugeZone
{
    /// <summary>How much of the sweep this band covers, in the scale's own units.</summary>
    public double Extent { get; init; }
    /// <summary>Already carries its own alpha: the view model dims the bands the
    /// reading isn't in, so the control never has to know which one is active.</summary>
    public Color Color { get; init; }
}

public enum GaugeKind
{
    /// <summary>180° band of coloured zones with a marker riding it.</summary>
    Arc,
    /// <summary>Full ring with N/E/S/W ticks and a pointer — for readings that are directions.</summary>
    Compass,
    /// <summary>The sun's path: a sine crossing a horizon line, marker on the curve.</summary>
    DayCurve,
    /// <summary>270° comb of plain ticks with one bold marker — for a range that has no
    /// hazard bands to colour, only a low end and a high end.</summary>
    Dial,
}

/// <summary>Everything a gauge face needs. Built by the view model, drawn by <see cref="ArcGauge"/>.</summary>
public sealed class GaugeSpec
{
    public GaugeKind Kind { get; init; }
    /// <summary>Where the marker sits along the sweep, 0 at the start and 1 at the end.</summary>
    public double Fraction { get; init; }
    public IReadOnlyList<GaugeZone> Zones { get; init; } = [];
    /// <summary>Numbers printed at the two feet of the arc. Empty hides them.</summary>
    public string MinLabel { get; init; } = "";
    public string MaxLabel { get; init; } = "";

    /// <summary>DayCurve only: where the curve crosses the horizon, as fractions of the width.
    /// The view model places them, so the control never has to know about clock time.</summary>
    public double RiseFraction { get; init; }
    public double SetFraction { get; init; }

    /// <summary>Dial only: the unit, set under the value so the reading itself stays big.</summary>
    public string UnitLabel { get; init; } = "";
}

/// <summary>
/// A gauge face drawn like a clock: the frame is printed (zone bands, boundary ticks,
/// end labels) so the reading is a <em>position</em> on a fixed face rather than a number
/// you have to know the scale for. Two modes — a 180° arc for scalar readings, and a full
/// compass ring for wind, because direction is genuinely circular.
/// The value sits inside the face, so the control owns its own layout.
/// </summary>
public sealed partial class ArcGauge : Canvas
{
    private const double BandThickness = 6;
    private const double LabelSize = 10;
    private const double ValueSize = 24;
    /// <summary>A shade smaller inside the compass, where the arrow has to get past it.</summary>
    private const double CompassValueSize = 21;
    /// <summary>Room under the arc's feet for the end labels.</summary>
    private const double LabelGutter = 15;
    private const double MaxArcRadius = 72;

    private static readonly SolidColorBrush TrackBrush = new(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TickBrush = new(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LabelBrush = new(Color.FromArgb(0x7D, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush InkBrush = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CurveBrush = new(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush NightBrush = new(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF));
    /// <summary>The amber the temperature range bars end on — the app's warm accent.</summary>
    private static readonly SolidColorBrush SunBrush = new(Color.FromArgb(0xFF, 0xFF, 0xD3, 0x6E));

    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(
        nameof(Spec), typeof(GaugeSpec), typeof(ArcGauge), new PropertyMetadata(null, OnRedraw));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ArcGauge), new PropertyMetadata("", OnRedraw));

    public GaugeSpec? Spec
    {
        get => (GaugeSpec?)GetValue(SpecProperty);
        set => SetValue(SpecProperty, value);
    }

    /// <summary>The reading itself, printed in the middle of the face.</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ArcGauge()
    {
        // the face is laid out from the control's actual box, so it has to be
        // rebuilt whenever the wrap grid restretches the card
        SizeChanged += (_, _) => Rebuild();
    }

    private static void OnRedraw(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ArcGauge)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (Spec is not { } spec || ActualWidth < 40 || ActualHeight < 40) return;

        switch (spec.Kind)
        {
            case GaugeKind.Compass: DrawCompass(spec); break;
            case GaugeKind.DayCurve: DrawDayCurve(spec); break;
            case GaugeKind.Dial: DrawDial(spec); break;
            default: DrawArc(spec); break;
        }
    }

    // ----- dial: a plain tick comb, no colour, one bold marker -----

    private void DrawDial(GaugeSpec spec)
    {
        const double tick = 7, marker = 14;
        const int count = 33;
        var (c, r) = ArcFace();

        for (int i = 0; i < count; i++)
        {
            AddTick(c, r, 180 + 180.0 * i / (count - 1), tick, 1.5, TickBrush);
        }
        AddTick(c, r, 180 + 180 * Math.Clamp(spec.Fraction, 0, 1), marker, 4, InkBrush, round: true);

        AddEndLabels(spec, c, r);

        // value over unit, so a four-digit reading still fits between the ticks
        double valueY = c.Y - r * 0.52;
        AddText(Value, ValueSize, InkBrush, c.X, valueY, centred: true, weight: Microsoft.UI.Text.FontWeights.SemiLight);
        AddText(spec.UnitLabel, LabelSize + 1, LabelBrush, c.X, valueY + ValueSize * 0.82, centred: true);
    }

    // ----- day curve: the sun's own path, dipping below a horizon line at each end -----

    private void DrawDayCurve(GaugeSpec spec)
    {
        double w = ActualWidth;
        // the reading rides above the curve rather than inside it — a horizon needs the
        // full width, and there is no bowl to sit in
        AddText(Value, ValueSize, InkBrush, 0, 0, centred: false, weight: Microsoft.UI.Text.FontWeights.SemiLight);

        double top = ValueSize * 1.45;
        double horizon = top + (ActualHeight - top) * 0.5;
        double amp = (ActualHeight - top) * 0.42;

        Children.Add(new Line
        {
            X1 = 0,
            Y1 = horizon,
            X2 = w,
            Y2 = horizon,
            Stroke = TrackBrush,
            StrokeThickness = 1,
        });

        // A sine pinned to zero at the two horizon crossings, so it is above the line for
        // exactly as much of the width as the day is long, and below it either side.
        double rise = spec.RiseFraction;
        double set = spec.SetFraction;
        double sweep = Math.Max(0.05, set - rise);
        Point At(double t) => new(t * w, horizon - Math.Sin(Math.PI * (t - rise) / sweep) * amp);

        AddCurve(At, 0, 1, NightBrush, 1.75);
        // the daylight stretch a touch brighter, the way the sky itself is
        AddCurve(At, rise, set, CurveBrush, 2);

        // the two crossings are sunrise and sunset; a notch each makes them findable
        foreach (double t in new[] { rise, set })
        {
            double x = t * w;
            Children.Add(new Line
            {
                X1 = x,
                Y1 = horizon - 4,
                X2 = x,
                Y2 = horizon + 4,
                Stroke = TickBrush,
                StrokeThickness = 1.25,
            });
        }

        var sun = At(Math.Clamp(spec.Fraction, 0, 1));
        AddDot(sun, SunBrush);
    }

    /// <summary>Samples a curve into a polyline — plenty smooth at this size.</summary>
    private void AddCurve(Func<double, Point> at, double from, double to, Brush stroke, double thickness)
    {
        const int steps = 48;
        var points = new PointCollection();
        for (int i = 0; i <= steps; i++)
        {
            points.Add(at(from + (to - from) * i / steps));
        }
        Children.Add(new Polyline
        {
            Points = points,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
    }

    // ----- 180° arc: left foot at 180°, sweeping clockwise over the top to 0° -----

    /// <summary>
    /// Centre and radius shared by every 180° face, so the banded arcs and the tick dial
    /// line up across a row of cards instead of each finding its own centre.
    /// </summary>
    private (Point Centre, double Radius) ArcFace()
    {
        double cx = ActualWidth / 2;
        double band = ActualHeight - LabelGutter;
        // capped, then centred in what's left: an arc as wide as the card would swamp it
        double r = Math.Min(Math.Min(cx, band) - BandThickness / 2 - 1, MaxArcRadius);
        return (new Point(cx, (band - r) / 2 + r), r);
    }

    private void DrawArc(GaugeSpec spec)
    {
        var (c, r) = ArcFace();
        double cx = c.X, cy = c.Y;

        // full track first, so a gauge whose zones don't fill the sweep still reads as a face
        AddArc(c, r, 180, 360, TrackBrush, BandThickness);

        double total = 0;
        foreach (var z in spec.Zones) total += z.Extent;
        if (total > 0)
        {
            double at = 0;
            for (int i = 0; i < spec.Zones.Count; i++)
            {
                var z = spec.Zones[i];
                if (z.Extent <= 0) continue;
                double a0 = 180 + at / total * 180;
                double a1 = 180 + (at + z.Extent) / total * 180;
                // hairline gap between bands so neighbouring colours stay distinct
                AddArc(c, r, a0 + (i == 0 ? 0 : 0.8), a1, new SolidColorBrush(z.Color), BandThickness);
                at += z.Extent;

                // boundary ticks are the printed frame; two zones means the single
                // boundary is the marker itself, so leave the face clean
                if (spec.Zones.Count >= 3 && i < spec.Zones.Count - 1) AddTick(c, r, a1, 4, 1.25, TickBrush);
            }
        }

        AddDot(Polar(c, r, 180 + Math.Clamp(spec.Fraction, 0, 1) * 180), InkBrush);
        AddEndLabels(spec, c, r);
        AddValue(cx, cy - r * 0.42);
    }

    /// <summary>End labels tucked under the face's feet, where they can't collide with the value.</summary>
    private void AddEndLabels(GaugeSpec spec, Point c, double r)
    {
        if (spec.MinLabel.Length > 0) AddText(spec.MinLabel, LabelSize, LabelBrush, c.X - r, c.Y + 8, centred: true);
        if (spec.MaxLabel.Length > 0) AddText(spec.MaxLabel, LabelSize, LabelBrush, c.X + r, c.Y + 8, centred: true);
    }

    // ----- compass: tick comb ringing an arrow that crosses the whole face -----

    private void DrawCompass(GaugeSpec spec)
    {
        double cx = ActualWidth / 2;
        double cy = ActualHeight / 2;
        var c = new Point(cx, cy);
        double r = Math.Min(cx, cy) - LabelSize - 4;

        // the same comb the pressure dial uses, closed into a full circle; the four
        // cardinals get a longer, brighter tick so the letters have something to sit on
        const int ticks = 72;
        for (int i = 0; i < ticks; i++)
        {
            bool cardinal = i % (ticks / 4) == 0;
            AddTick(c, r, -90 + i * 360.0 / ticks, cardinal ? 8 : 5, cardinal ? 1.5 : 1, cardinal ? LabelBrush : TickBrush);
        }

        string[] cardinals = ["N", "E", "S", "W"];
        for (int i = 0; i < 4; i++)
        {
            var p = Polar(c, r + LabelSize, -90 + i * 90);
            AddText(cardinals[i], LabelSize, LabelBrush, p.X, p.Y, centred: true);
        }

        // The reading is measured before the arrow is drawn so the shaft can be cut back to
        // the edge of the text's box. A single hub radius won't do it: the block is far
        // wider than it is tall, so a gap that clears "km/h" sideways would leave a stub
        // vertically, and one that fits vertically runs straight through the unit.
        double valueY = cy - CompassValueSize * 0.2;
        var valueBlock = MakeText(Value, CompassValueSize, InkBrush, cx, valueY, centred: true,
            weight: Microsoft.UI.Text.FontWeights.SemiBold);
        var unitBlock = MakeText(spec.UnitLabel, LabelSize, LabelBrush, cx,
            valueY + CompassValueSize * 0.78, centred: true);

        double halfWidth = 0, top = cy, bottom = cy;
        foreach (var block in new[] { valueBlock, unitBlock })
        {
            if (block is null) continue;
            halfWidth = Math.Max(halfWidth, block.DesiredSize.Width / 2);
            top = Math.Min(top, block.Margin.Top);
            bottom = Math.Max(bottom, block.Margin.Top + block.DesiredSize.Height);
        }

        double from = -90 + Math.Clamp(spec.Fraction, 0, 1) * 360;
        double to = from + 180;
        double reach = r - 8;

        // where a ray leaves that box, plus a breath of clearance
        double Gap(double degrees)
        {
            double a = degrees * Math.PI / 180;
            double dx = Math.Cos(a), dy = Math.Sin(a);
            double tx = Math.Abs(dx) < 1e-6 ? double.MaxValue : ((dx > 0 ? halfWidth : -halfWidth) / dx);
            double ty = Math.Abs(dy) < 1e-6 ? double.MaxValue : ((dy > 0 ? bottom - cy : top - cy) / dy);
            return Math.Min(Math.Min(tx, ty) + 5, reach - 5);
        }

        AddLine(Polar(c, reach, from), Polar(c, Gap(from), from), InkBrush, 2.5);
        AddLine(Polar(c, Gap(to), to), Polar(c, reach - 3, to), InkBrush, 2.5);
        AddArrowHead(c, reach - 3, to);

        // the tail marks the quarter the wind comes from; the head shows where it is headed
        const double dot = 7;
        var tail = Polar(c, reach, from);
        Children.Add(new Ellipse
        {
            Width = dot,
            Height = dot,
            Fill = InkBrush,
            Margin = new Thickness(tail.X - dot / 2, tail.Y - dot / 2, 0, 0),
        });

        if (valueBlock is not null) Children.Add(valueBlock);
        if (unitBlock is not null) Children.Add(unitBlock);
    }

    /// <summary>Arrowhead built off the shaft's own direction, so it stays the same size
    /// whatever radius it sits at.</summary>
    private void AddArrowHead(Point c, double reach, double angle)
    {
        double a = angle * Math.PI / 180;
        double dx = Math.Cos(a), dy = Math.Sin(a);
        Point At(double along, double across) =>
            new(c.X + dx * along - dy * across, c.Y + dy * along + dx * across);
        Children.Add(new Polygon
        {
            Points = [At(reach + 7, 0), At(reach - 3, 5.5), At(reach - 3, -5.5)],
            Fill = InkBrush,
        });
    }

    private void AddLine(Point a, Point b, Brush stroke, double thickness) =>
        Children.Add(new Line
        {
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

    // ----- primitives -----

    private void AddArc(Point c, double r, double from, double to, Brush stroke, double thickness)
    {
        var figure = new PathFigure { StartPoint = Polar(c, r, from), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = Polar(c, r, to),
            Size = new Size(r, r),
            IsLargeArc = Math.Abs(to - from) > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        Children.Add(new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
    }

    private void AddTick(Point c, double r, double angle, double length, double thickness, Brush stroke, bool round = false)
    {
        var a = Polar(c, r - length / 2, angle);
        var b = Polar(c, r + length / 2, angle);
        var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = thickness };
        if (round)
        {
            line.StrokeStartLineCap = PenLineCap.Round;
            line.StrokeEndLineCap = PenLineCap.Round;
        }
        Children.Add(line);
    }

    /// <summary>The marker: a bright dot riding the face, with a dark collar so it stays
    /// readable over whichever colour it lands on.</summary>
    private void AddDot(Point p, Brush fill)
    {
        const double outer = 9;
        Children.Add(new Ellipse
        {
            Width = outer,
            Height = outer,
            Fill = new SolidColorBrush(Color.FromArgb(0x8A, 0x18, 0x23, 0x38)),
            Margin = new Thickness(p.X - outer / 2, p.Y - outer / 2, 0, 0),
        });
        const double inner = 6;
        Children.Add(new Ellipse
        {
            Width = inner,
            Height = inner,
            Fill = fill,
            Margin = new Thickness(p.X - inner / 2, p.Y - inner / 2, 0, 0),
        });
    }

    private void AddValue(double cx, double cy) =>
        AddText(Value, ValueSize, InkBrush, cx, cy, centred: true, weight: Microsoft.UI.Text.FontWeights.SemiLight);

    private void AddText(string text, double size, Brush brush, double x, double y, bool centred, Windows.UI.Text.FontWeight? weight = null)
    {
        if (MakeText(text, size, brush, x, y, centred, weight) is { } block) Children.Add(block);
    }

    /// <summary>
    /// Builds and positions text by its own measured size — Canvas won't centre anything for
    /// us — but leaves adding it to the caller, so a face can measure its middle before
    /// deciding how much room the artwork has to leave.
    /// </summary>
    private static TextBlock? MakeText(string text, double size, Brush brush, double x, double y, bool centred, Windows.UI.Text.FontWeight? weight = null)
    {
        if (text.Length == 0) return null;
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            IsHitTestVisible = false,
        };
        if (weight is { } w) block.FontWeight = w;
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = centred ? x - block.DesiredSize.Width / 2 : x;
        double top = centred ? y - block.DesiredSize.Height / 2 : y;
        block.Margin = new Thickness(left, top, 0, 0);
        return block;
    }

    private static Point Polar(Point c, double r, double angleDegrees)
    {
        double a = angleDegrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }
}
