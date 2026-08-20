using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BladeFanCurve.Control;

namespace BladeFanCurve.UI;

/// <summary>
/// A 30-minute rolling chart of CPU and GPU package power.
///
/// Both series are watts, so they share one y-axis — two scales on one plot would
/// make the lines look comparable when they are not. Time runs left (30 minutes ago)
/// to right (now). Gaps in the data are drawn as breaks rather than joined, so a
/// period where nothing was measured cannot be mistaken for a period of zero draw.
/// </summary>
public sealed class PowerChart : FrameworkElement
{
    // Series colours: the app's CPU green and GPU violet, stepped down into the
    // lightness band that keeps them legible as large filled areas on the dark card.
    // Checked for colour-vision separation rather than eyeballed — deutan ΔE 23.5,
    // normal-vision ΔE 26.5, both well clear of the floors.
    private static readonly Color CpuColor = Color.FromRgb(0x3A, 0xAD, 0x77);
    private static readonly Color GpuColor = Color.FromRgb(0x60, 0x70, 0xDE);

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2B)));
    private static readonly Brush AxisTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x77, 0x84)));
    private static readonly Brush DimTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x55, 0x5F)));
    private static readonly Brush CrosshairBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x44, 0x4F)));

    private readonly Pen _cpuPen;
    private readonly Pen _gpuPen;
    private readonly Pen _gridPen;
    private readonly Pen _crosshairPen;
    private readonly Brush _cpuFill;
    private readonly Brush _gpuFill;
    private readonly Brush _cpuMarker;
    private readonly Brush _gpuMarker;
    private readonly Pen _markerRing;

    private IReadOnlyList<PowerSample> _samples = Array.Empty<PowerSample>();
    private double _peak = 60;
    private Point? _cursor;

    /// <summary>Shown instead of the plot when there is nothing worth drawing.</summary>
    public string? EmptyMessage { get; set; }

    /// <summary>Explains a missing series without pretending it reads zero.</summary>
    public string? CpuUnavailableNote { get; set; }

    public PowerChart()
    {
        _cpuPen = Freeze(new Pen(new SolidColorBrush(CpuColor), 2));
        _gpuPen = Freeze(new Pen(new SolidColorBrush(GpuColor), 2));
        _gridPen = Freeze(new Pen(GridBrush, 1));
        _crosshairPen = Freeze(new Pen(CrosshairBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });

        _cpuFill = Freeze(VerticalFade(CpuColor));
        _gpuFill = Freeze(VerticalFade(GpuColor));

        _cpuMarker = Freeze(new SolidColorBrush(CpuColor));
        _gpuMarker = Freeze(new SolidColorBrush(GpuColor));
        _markerRing = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x17)), 2));

        ClipToBounds = true;
    }

    private static LinearGradientBrush VerticalFade(Color c) => new(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x3D, c.R, c.G, c.B), 0),
            new(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
        },
        new Point(0, 0), new Point(0, 1));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public void Update(IReadOnlyList<PowerSample> samples, double peakWatts)
    {
        _samples = samples;
        // Round the ceiling up to a friendly step so the gridlines land on whole
        // numbers, and never let it collapse so far that noise looks dramatic.
        _peak = NiceCeiling(Math.Max(peakWatts, 20));
        InvalidateVisual();
    }

    private static double NiceCeiling(double v)
    {
        foreach (var step in new[] { 20, 25, 40, 50, 60, 80, 100, 125, 150, 200, 250, 300 })
            if (v <= step) return step;

        return Math.Ceiling(v / 50) * 50;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _cursor = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _cursor = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 20 || h <= 20) return;

        // Room for the axis labels without a heavyweight axis.
        const double leftPad = 46;
        const double bottomPad = 22;
        const double topPad = 10;

        var plot = new Rect(leftPad, topPad, Math.Max(1, w - leftPad - 8),
            Math.Max(1, h - topPad - bottomPad));

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit testing

        DrawGrid(dc, plot);

        if (_samples.Count < 2)
        {
            DrawCentredText(dc, plot, EmptyMessage ?? "Collecting…");
            return;
        }

        // GPU first so the CPU line sits on top; CPU is the one being tuned.
        DrawSeries(dc, plot, s => s.GpuWatts, _gpuPen, _gpuFill);
        DrawSeries(dc, plot, s => s.CpuWatts, _cpuPen, _cpuFill);

        DrawCrosshair(dc, plot);
    }

    private void DrawGrid(DrawingContext dc, Rect plot)
    {
        // Horizontal: four watt gridlines is enough to read a level without turning
        // the plot into graph paper.
        for (var i = 0; i <= 4; i++)
        {
            var value = _peak * i / 4.0;
            var y = Math.Round(plot.Bottom - plot.Height * i / 4.0) + 0.5;

            dc.DrawLine(_gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            var label = Text($"{value:0} W", 11, AxisTextBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        // Vertical: one every ten minutes, labelled by age.
        for (var minutes = 0; minutes <= 30; minutes += 10)
        {
            var x = Math.Round(plot.Right - plot.Width * minutes / 30.0) + 0.5;
            dc.DrawLine(_gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var label = Text(minutes == 0 ? "now" : $"-{minutes}m", 11, DimTextBrush);
            var tx = Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width);
            dc.DrawText(label, new Point(tx, plot.Bottom + 5));
        }
    }

    /// <summary>
    /// Draws one series as a set of contiguous runs. A null reading ends the current
    /// run, so a gap stays a gap instead of becoming a straight line at an invented
    /// value.
    /// </summary>
    private void DrawSeries(DrawingContext dc, Rect plot, Func<PowerSample, double?> pick,
        Pen pen, Brush fill)
    {
        var run = new List<Point>();

        void Flush()
        {
            if (run.Count >= 2)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    // Filled area first: up from the baseline, across the run, back down.
                    ctx.BeginFigure(new Point(run[0].X, plot.Bottom), true, true);
                    ctx.LineTo(run[0], false, false);
                    ctx.PolyLineTo(run.Skip(1).ToList(), true, true);
                    ctx.LineTo(new Point(run[^1].X, plot.Bottom), false, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(fill, null, geometry);

                var line = new StreamGeometry();
                using (var ctx = line.Open())
                {
                    ctx.BeginFigure(run[0], false, false);
                    ctx.PolyLineTo(run.Skip(1).ToList(), true, true);
                }
                line.Freeze();
                dc.DrawGeometry(null, pen, line);
            }
            else if (run.Count == 1)
            {
                // A lone sample would otherwise be invisible.
                dc.DrawEllipse(pen.Brush, null, run[0], 1.6, 1.6);
            }

            run.Clear();
        }

        foreach (var sample in _samples)
        {
            if (pick(sample) is not { } value)
            {
                Flush();
                continue;
            }

            run.Add(new Point(XFor(plot, sample.AgeSeconds), YFor(plot, value)));
        }

        Flush();
    }

    private static double XFor(Rect plot, double ageSeconds) =>
        plot.Right - plot.Width * Math.Clamp(ageSeconds, 0, PowerHistory.WindowSeconds) / PowerHistory.WindowSeconds;

    private double YFor(Rect plot, double watts) =>
        plot.Bottom - plot.Height * Math.Clamp(watts / _peak, 0, 1);

    private void DrawCrosshair(DrawingContext dc, Rect plot)
    {
        if (_cursor is not { } cursor) return;
        if (cursor.X < plot.Left || cursor.X > plot.Right) return;

        var age = (plot.Right - cursor.X) / plot.Width * PowerHistory.WindowSeconds;

        // Nearest sample in time, so the readout matches a real measurement rather
        // than an interpolated one.
        PowerSample? nearest = null;
        var bestGap = double.MaxValue;
        foreach (var s in _samples)
        {
            var gap = Math.Abs(s.AgeSeconds - age);
            if (gap >= bestGap) continue;
            bestGap = gap;
            nearest = s;
        }

        if (nearest is not { } hit) return;

        var x = Math.Round(XFor(plot, hit.AgeSeconds)) + 0.5;
        dc.DrawLine(_crosshairPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        // A ring of the card colour around each marker keeps it readable where the two
        // series cross.
        foreach (var (value, brush) in new[] { (hit.CpuWatts, _cpuMarker), (hit.GpuWatts, _gpuMarker) })
        {
            if (value is not { } v) continue;
            dc.DrawEllipse(brush, _markerRing, new Point(x, YFor(plot, v)), 4, 4);
        }

        var minutes = hit.AgeSeconds / 60.0;
        var when = hit.AgeSeconds < 5 ? "now" : $"{minutes:0.0} min ago";
        var cpu = hit.CpuWatts is { } c ? $"CPU {c:0.0} W" : "CPU —";
        var gpu = hit.GpuWatts is { } g ? $"GPU {g:0.0} W" : "GPU —";

        var text = Text($"{when}    {cpu}    {gpu}", 11, AxisTextBrush);
        var bx = Math.Clamp(x + 10, plot.Left, Math.Max(plot.Left, plot.Right - text.Width - 6));
        dc.DrawText(text, new Point(bx, plot.Top + 2));
    }

    private void DrawCentredText(DrawingContext dc, Rect plot, string message)
    {
        var text = Text(message, 12, AxisTextBrush);
        text.MaxTextWidth = Math.Max(80, plot.Width - 40);
        dc.DrawText(text, new Point(
            plot.Left + Math.Max(0, (plot.Width - text.Width) / 2),
            plot.Top + Math.Max(0, (plot.Height - text.Height) / 2)));
    }

    private FormattedText Text(string s, double size, Brush brush) => new(
        s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
