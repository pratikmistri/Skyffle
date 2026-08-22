using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Skyffle.ViewModels;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path; // implicit usings pull in System.IO.Path

namespace Skyffle.Controls;

/// <summary>
/// One day's temperature curve, drawn as XAML shapes on a canvas: a smoothed spline over
/// the day's hours, fill and stroke keyed to absolute temperature (cool blue low, warm
/// amber high), condition glyphs along the top, a precipitation band underneath, and a
/// temperature axis on the right. Hovering snaps a crosshair to the nearest hour and
/// reads out its exact numbers.
/// </summary>
/// <remarks>
/// Shapes rather than a second Win2D surface: the sky already owns a swap chain, and a
/// canvas per expanded row would cost far more than the ~80 elements this builds. Nothing
/// is drawn until the row is sized and on screen, so collapsed rows carry no geometry.
/// </remarks>
public sealed class HourlyChart : Grid
{
    // plot furniture, in effective pixels
    private const double AxisWidth = 40;   // right gutter for the temperature labels
    private const double GlyphStrip = 26;  // top strip for the condition glyphs
    private const double TimeStrip = 18;   // bottom strip for the hour labels
    private const double PrecipBand = 30;  // band for the precipitation bars, when there are any

    private static readonly Color Cool = Color.FromArgb(0xFF, 0x6E, 0xC1, 0xFF);
    private static readonly Color Warm = Color.FromArgb(0xFF, 0xFF, 0xD3, 0x6E);

    private readonly Canvas canvas = new();

    // hover furniture, rebuilt with the rest of the chart and parked at zero opacity
    private Rectangle? crosshair;
    private Ellipse? dot;
    private Border? readout;
    private TextBlock? readTime;
    private TextBlock? readTemp;
    private TextBlock? readNote;

    private IReadOnlyList<HourPoint> points = [];
    private double[] xs = [];
    private double[] ys = [];
    private bool parentHooked;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<HourPoint>), typeof(HourlyChart),
        new PropertyMetadata(null, (d, _) => ((HourlyChart)d).Rebuild()));

    public IReadOnlyList<HourPoint>? Points
    {
        get => (IReadOnlyList<HourPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public HourlyChart()
    {
        // a transparent plate so the pointer is tracked across the empty parts of the plot
        Background = new SolidColorBrush(Colors.Transparent);
        Children.Add(canvas);

        SizeChanged += (_, _) => Rebuild();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => HideReadout();
        PointerCanceled += (_, _) => HideReadout();
        PointerCaptureLost += (_, _) => HideReadout();

        // the row's expansion is a Visibility flip on our parent, which raises no event of
        // its own; watch the property so the curve can ease in each time the row opens
        Loaded += (_, _) =>
        {
            if (!parentHooked && Parent is FrameworkElement parent)
            {
                parentHooked = true;
                parent.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
                {
                    if (parent.Visibility == Visibility.Visible) PlayIntro();
                    else HideReadout();
                });
            }
        };
    }

    private void PlayIntro()
    {
        RenderTransform = new TranslateTransform { Y = -10 };
        var sb = new Storyboard();
        sb.Children.Add(Ease(this, "Opacity", 0, 1));
        sb.Children.Add(Ease(this, "(UIElement.RenderTransform).(TranslateTransform.Y)", -10, 0));
        sb.Begin();
    }

    private static DoubleAnimation Ease(DependencyObject target, string path, double from, double to)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path);
        return anim;
    }

    private void Rebuild()
    {
        canvas.Children.Clear();
        crosshair = null;
        dot = null;
        readout = null;
        xs = [];
        ys = [];
        points = Points ?? [];

        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60 || points.Count < 2) return;

        // a band of 2px stubs reads as a dashed rule, not as data; below a real chance
        // of rain the bars say nothing the curve doesn't, so the band is left off
        bool hasPrecip = points.Any(p => p.PrecipProbability >= 15);
        double precipH = hasPrecip ? PrecipBand : 0;
        double left = 2, right = w - AxisWidth;
        double top = GlyphStrip, bottom = h - TimeStrip - precipH;
        if (right - left < 40 || bottom - top < 30) return;

        // ----- vertical scale: round bounds sitting a little clear of the day's extremes -----
        double tMin = points.Min(p => p.Temp), tMax = points.Max(p => p.Temp);
        double step = tMax - tMin > 26 ? 10 : 5;
        double lo = Math.Floor((tMin - step * 0.5) / step) * step;
        double hi = Math.Ceiling((tMax + step * 0.5) / step) * step;
        if (hi - lo < step * 2) hi = lo + step * 2;
        double Y(double t) => bottom - (t - lo) / (hi - lo) * (bottom - top);

        for (double v = lo; v <= hi + 0.01; v += step)
        {
            double y = Y(v);
            Place(new Rectangle
            {
                Width = right - left,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            }, left, y);
            Place(new TextBlock
            {
                Text = $"{v:0}°",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(0x7D, 0xFF, 0xFF, 0xFF)),
            }, right + 8, y - 9);
        }

        // ----- point geometry -----
        int n = points.Count;
        xs = new double[n];
        ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = left + (right - left) * (double)i / (n - 1);
            ys[i] = Y(points[i].Temp);
        }

        // absolute mapping so a colour always means the same temperature: a flat day reads
        // as one steady tone instead of stretching the whole ramp across a few pixels
        var fillBrush = AbsoluteRamp(top, bottom,
            Color.FromArgb(0x66, Warm.R, Warm.G, Warm.B),
            Color.FromArgb(0x14, Cool.R, Cool.G, Cool.B));
        var strokeBrush = AbsoluteRamp(top, bottom, Warm, Cool);

        Place(new Path { Data = Spline(xs, ys, bottom), Fill = fillBrush, IsHitTestVisible = false }, 0, 0);
        Place(new Path
        {
            Data = Spline(xs, ys, null),
            Stroke = strokeBrush,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        }, 0, 0);

        // ----- precipitation bars, standing on their own rule below the curve -----
        if (hasPrecip)
        {
            double barBase = h - TimeStrip - 4;
            double barW = Math.Max(3, (right - left) / n * 0.6);
            Place(new Rectangle
            {
                Width = right - left,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x1F, 0x9C, 0xD2, 0xFF)),
            }, left, barBase);
            // the band always runs 0–100%, so it is the scale that is worth naming, not a tick
            Place(new TextBlock
            {
                Text = "RAIN",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0x9C, 0xD2, 0xFF)),
            }, right + 8, barBase - 11);
            for (int i = 0; i < n; i++)
            {
                double p = points[i].PrecipProbability ?? 0;
                if (p < 5) continue;
                double barH = Math.Max(2, p / 100.0 * (precipH - 10));
                Place(new Rectangle
                {
                    Width = barW,
                    Height = barH,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0x9C, 0xD2, 0xFF)),
                }, xs[i] - barW / 2, barBase - barH);
            }
        }

        // ----- condition glyphs, thinned until they stop colliding -----
        int stride = Math.Max(1, (int)Math.Ceiling(n * 26.0 / (right - left)));
        for (int i = 0; i < n; i += stride)
        {
            Place(new TextBlock { Text = points[i].Glyph, FontSize = 14 }, xs[i] - 9, 1);
        }

        // ----- hour labels every six hours -----
        for (int i = 0; i < n; i++)
        {
            if (points[i].Time.Hour % 6 != 0) continue;
            Place(new TextBlock
            {
                Text = ShortHour(points[i].Time),
                FontSize = 11,
                Width = 44,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0x7D, 0xFF, 0xFF, 0xFF)),
            }, Math.Clamp(xs[i] - 22, 0, Math.Max(0, right - 44)), h - TimeStrip + 1);
        }

        // ----- "now" marker, today's row only -----
        int nowIndex = IndexOfNow();
        if (nowIndex >= 0)
        {
            Place(new Rectangle
            {
                Width = 1,
                Height = bottom - top,
                Fill = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)),
            }, xs[nowIndex], top);
            Place(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Colors.White),
            }, xs[nowIndex] - 3.5, ys[nowIndex] - 3.5);
        }

        BuildHoverLayer(top, bottom + precipH);
    }

    private static LinearGradientBrush AbsoluteRamp(double top, double bottom, Color warm, Color cool)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, top),
            EndPoint = new Point(0, bottom),
        };
        brush.GradientStops.Add(new GradientStop { Color = warm, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = cool, Offset = 1 });
        return brush;
    }

    private void BuildHoverLayer(double top, double bottom)
    {
        crosshair = new Rectangle
        {
            Width = 1,
            Height = Math.Max(1, bottom - top),
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Place(crosshair, 0, top);

        dot = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x21, 0x3A)),
            StrokeThickness = 2,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Place(dot, 0, 0);

        readTime = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)) };
        readTemp = new TextBlock
        {
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
        };
        readNote = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)) };

        var stack = new StackPanel();
        stack.Children.Add(readTime);
        stack.Children.Add(readTemp);
        stack.Children.Add(readNote);

        readout = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x18, 0x23, 0x38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 8),
            Child = stack,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Place(readout, 0, 0);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (xs.Length == 0 || readout is null || crosshair is null || dot is null) return;

        double px = e.GetCurrentPoint(this).Position.X;
        int idx = 0;
        for (int i = 1; i < xs.Length; i++)
        {
            if (Math.Abs(xs[i] - px) < Math.Abs(xs[idx] - px)) idx = i;
        }

        var p = points[idx];
        readTime!.Text = p.IsNow ? "Now" : ShortHour(p.Time);
        readTemp!.Text = $"{Math.Round(p.Temp)}°  {p.Glyph}";
        // a 2% chance is noise dressed up as a reading; it says nothing worth the line
        readNote!.Text = p.PrecipProbability is >= 10
            ? $"{p.Description} · {p.PrecipProbability:0}% precip"
            : p.Description;

        // remeasure: the readout is sized by its text, and that changes with every hour
        readout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double rw = readout.DesiredSize.Width, rh = readout.DesiredSize.Height;
        double x = xs[idx], y = ys[idx];
        // below the point by default and above only when it would fall off the bottom:
        // one rule the whole way across, so the card doesn't flip sides as you sweep
        double ry = y + 16;
        if (ry + rh > ActualHeight) ry = Math.Max(0, y - rh - 16);

        Canvas.SetLeft(readout, Math.Clamp(x - rw / 2, 0, Math.Max(0, ActualWidth - rw)));
        Canvas.SetTop(readout, ry);
        Canvas.SetLeft(crosshair, x);
        Canvas.SetLeft(dot, x - 5.5);
        Canvas.SetTop(dot, y - 5.5);
        crosshair.Opacity = dot.Opacity = readout.Opacity = 1;
    }

    private void HideReadout()
    {
        if (crosshair is not null) crosshair.Opacity = 0;
        if (dot is not null) dot.Opacity = 0;
        if (readout is not null) readout.Opacity = 0;
    }

    private int IndexOfNow()
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].IsNow) return i;
        }
        return -1;
    }

    private void Place(FrameworkElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        canvas.Children.Add(el);
    }

    private static string ShortHour(DateTime t) =>
        t.ToString("h tt", CultureInfo.InvariantCulture).Replace(" ", "").ToUpperInvariant();

    /// <summary>
    /// Catmull-Rom through every hour, converted to the cubic Beziers a PathGeometry
    /// speaks. With <paramref name="baseline"/> set, the figure drops to it and closes,
    /// giving the filled area; without, it is the bare curve.
    /// </summary>
    private static PathGeometry Spline(double[] xs, double[] ys, double? baseline)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(xs[0], ys[0]),
            IsClosed = baseline is not null,
            IsFilled = baseline is not null,
        };
        for (int i = 0; i < xs.Length - 1; i++)
        {
            int prev = Math.Max(0, i - 1);
            int next = Math.Min(xs.Length - 1, i + 2);
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(xs[i] + (xs[i + 1] - xs[prev]) / 6.0, ys[i] + (ys[i + 1] - ys[prev]) / 6.0),
                Point2 = new Point(xs[i + 1] - (xs[next] - xs[i]) / 6.0, ys[i + 1] - (ys[next] - ys[i]) / 6.0),
                Point3 = new Point(xs[i + 1], ys[i + 1]),
            });
        }
        if (baseline is double b)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(xs[^1], b) });
            figure.Segments.Add(new LineSegment { Point = new Point(xs[0], b) });
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
