using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BladeFanCurve.Config;

namespace BladeFanCurve.UI;

/// <summary>
/// Draggable temperature → RPM curve editor.
///
///   left drag        move a point
///   double click     add a point
///   right click      remove a point
///
/// Segments are drawn straight because the evaluator interpolates linearly — a
/// smoothed spline would look nicer but would misrepresent what the fan actually
/// does between two points.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    private const double HandleRadius = 6.5;
    private const double HitRadius = 12.0;
    private const double Pad = 8;

    private static readonly int[] RpmGridSteps = { 500, 1000 };

    private int _dragIndex = -1;
    private Point _mouse;
    private bool _mouseInside;

    public event EventHandler? CurveChanged;

    // ------------------------------------------------------------ properties

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(FanCurveConfig), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FanCurveConfig? Curve
    {
        get => (FanCurveConfig?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public static readonly DependencyProperty MinRpmProperty = DependencyProperty.Register(
        nameof(MinRpm), typeof(int), typeof(CurveEditor),
        new FrameworkPropertyMetadata(2000, FrameworkPropertyMetadataOptions.AffectsRender));

    public int MinRpm
    {
        get => (int)GetValue(MinRpmProperty);
        set => SetValue(MinRpmProperty, value);
    }

    public static readonly DependencyProperty MaxRpmProperty = DependencyProperty.Register(
        nameof(MaxRpm), typeof(int), typeof(CurveEditor),
        new FrameworkPropertyMetadata(5000, FrameworkPropertyMetadataOptions.AffectsRender));

    public int MaxRpm
    {
        get => (int)GetValue(MaxRpmProperty);
        set => SetValue(MaxRpmProperty, value);
    }

    public static readonly DependencyProperty MinTempProperty = DependencyProperty.Register(
        nameof(MinTemp), typeof(double), typeof(CurveEditor),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double MinTemp
    {
        get => (double)GetValue(MinTempProperty);
        set => SetValue(MinTempProperty, value);
    }

    public static readonly DependencyProperty MaxTempProperty = DependencyProperty.Register(
        nameof(MaxTemp), typeof(double), typeof(CurveEditor),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double MaxTemp
    {
        get => (double)GetValue(MaxTempProperty);
        set => SetValue(MaxTempProperty, value);
    }

    public static readonly DependencyProperty CurrentTempProperty = DependencyProperty.Register(
        nameof(CurrentTemp), typeof(double?), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? CurrentTemp
    {
        get => (double?)GetValue(CurrentTempProperty);
        set => SetValue(CurrentTempProperty, value);
    }

    public static readonly DependencyProperty CurrentRpmProperty = DependencyProperty.Register(
        nameof(CurrentRpm), typeof(int), typeof(CurveEditor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int CurrentRpm
    {
        get => (int)GetValue(CurrentRpmProperty);
        set => SetValue(CurrentRpmProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(CurveEditor),
        new FrameworkPropertyMetadata(Color.FromRgb(0x5F, 0xD6, 0x9C),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public CurveEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 200;
        Cursor = Cursors.Cross;
    }

    // ------------------------------------------------------------ geometry

    private Rect PlotArea => new(
        Pad, Pad,
        Math.Max(10, ActualWidth - Pad * 2),
        Math.Max(10, ActualHeight - Pad * 2));

    private Point ToScreen(double tempC, double rpm)
    {
        var a = PlotArea;
        var tx = (tempC - MinTemp) / Math.Max(1, MaxTemp - MinTemp);
        var ty = (rpm - MinRpm) / Math.Max(1, MaxRpm - MinRpm);
        return new Point(a.Left + tx * a.Width, a.Bottom - ty * a.Height);
    }

    private (double tempC, int rpm) ToData(Point p)
    {
        var a = PlotArea;
        var tx = Math.Clamp((p.X - a.Left) / a.Width, 0, 1);
        var ty = Math.Clamp((a.Bottom - p.Y) / a.Height, 0, 1);
        var tempC = Math.Round(MinTemp + tx * (MaxTemp - MinTemp));
        var rpm = (int)(Math.Round((MinRpm + ty * (MaxRpm - MinRpm)) / 100.0) * 100);
        return (tempC, Math.Clamp(rpm, MinRpm, MaxRpm));
    }

    // ------------------------------------------------------------ input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var pos = e.GetPosition(this);
        var index = HitTest(pos);

        if (e.ClickCount >= 2 && index < 0)
        {
            AddPointAt(pos);
            return;
        }

        if (index >= 0)
        {
            _dragIndex = index;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        _mouseInside = true;

        if (_dragIndex < 0)
        {
            Cursor = HitTest(_mouse) >= 0 ? Cursors.Hand : Cursors.Cross;
            InvalidateVisual();
            return;
        }

        var curve = Curve;
        if (curve == null || _dragIndex >= curve.Points.Count) return;

        var (tempC, rpm) = ToData(_mouse);

        // Keep points ordered: a point may not pass its neighbours.
        var lower = _dragIndex > 0 ? curve.Points[_dragIndex - 1].TempC + 1 : MinTemp;
        var upper = _dragIndex < curve.Points.Count - 1 ? curve.Points[_dragIndex + 1].TempC - 1 : MaxTemp;
        if (upper < lower) upper = lower;

        curve.Points[_dragIndex].TempC = Math.Clamp(tempC, lower, upper);
        curve.Points[_dragIndex].Rpm = rpm;

        InvalidateVisual();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex >= 0)
        {
            _dragIndex = -1;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var curve = Curve;
        if (curve == null || curve.Points.Count <= 2) return;

        var index = HitTest(e.GetPosition(this));
        if (index < 0) return;

        curve.Points.RemoveAt(index);
        InvalidateVisual();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseInside = false;
        InvalidateVisual();
    }

    private void AddPointAt(Point pos)
    {
        var curve = Curve;
        if (curve == null || curve.Points.Count >= 16) return;

        var (tempC, rpm) = ToData(pos);
        curve.Points.Add(new CurvePoint(tempC, rpm));
        curve.Points = curve.Points.OrderBy(p => p.TempC).ToList();

        InvalidateVisual();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private int HitTest(Point p)
    {
        var curve = Curve;
        if (curve == null) return -1;

        for (var i = 0; i < curve.Points.Count; i++)
        {
            var s = ToScreen(curve.Points[i].TempC, curve.Points[i].Rpm);
            if ((s - p).Length <= HitRadius) return i;
        }

        return -1;
    }

    // ------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        var area = PlotArea;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Transparent hit area so the whole control receives mouse events.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1);
        gridPen.Freeze();

        var accentBrush = new SolidColorBrush(Accent);
        accentBrush.Freeze();

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x3A, Accent.R, Accent.G, Accent.B),
            Color.FromArgb(0x00, Accent.R, Accent.G, Accent.B),
            new Point(0, 0), new Point(0, 1));
        fill.Freeze();

        var linePen = new Pen(accentBrush, 2.4) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();

        // grid
        var rpmStep = (MaxRpm - MinRpm) > 4000 ? RpmGridSteps[1] : RpmGridSteps[0];
        for (var rpm = RoundUpTo(MinRpm, rpmStep); rpm <= MaxRpm; rpm += rpmStep)
        {
            var y = ToScreen(MinTemp, rpm).Y;
            if (y <= area.Top + 1 || y >= area.Bottom - 1) continue;
            dc.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
        }

        for (var t = RoundUpTo((int)MinTemp, 15); t <= MaxTemp; t += 15)
        {
            var x = ToScreen(t, MinRpm).X;
            if (x <= area.Left + 1 || x >= area.Right - 1) continue;
            dc.DrawLine(gridPen, new Point(x, area.Top), new Point(x, area.Bottom));
        }

        var curve = Curve;
        if (curve is not { Points.Count: > 0 }) return;

        var points = curve.Points.OrderBy(p => p.TempC).ToList();

        // The evaluator holds the first and last RPM outside the curve, so the drawing
        // runs flat to both edges rather than stopping at the outermost point.
        var line = new List<Point> { ToScreen(MinTemp, points[0].Rpm) };
        line.AddRange(points.Select(p => ToScreen(p.TempC, p.Rpm)));
        line.Add(ToScreen(MaxTemp, points[^1].Rpm));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(line[0].X, area.Bottom), true, true);
            foreach (var p in line) ctx.LineTo(p, true, false);
            ctx.LineTo(new Point(line[^1].X, area.Bottom), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, null, geometry);

        for (var i = 0; i < line.Count - 1; i++)
            dc.DrawLine(linePen, line[i], line[i + 1]);

        // current operating point
        if (CurrentTemp is { } liveTemp && liveTemp > 0)
        {
            var clamped = Math.Clamp(liveTemp, MinTemp, MaxTemp);
            var x = ToScreen(clamped, MinRpm).X;

            var markerPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1)
            {
                DashStyle = new DashStyle(new double[] { 3, 4 }, 0)
            };
            markerPen.Freeze();
            dc.DrawLine(markerPen, new Point(x, area.Top), new Point(x, area.Bottom));

            if (CurrentRpm > 0)
            {
                var dot = ToScreen(clamped, Math.Clamp(CurrentRpm, MinRpm, MaxRpm));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, dot, 8, 8);
                dc.DrawEllipse(Brushes.White, null, dot, 4, 4);
            }
        }

        // handles: hollow rings, filled with the card colour
        var handleFill = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x17));
        handleFill.Freeze();
        var handlePen = new Pen(accentBrush, 2.2);
        handlePen.Freeze();

        foreach (var p in points)
        {
            var s = ToScreen(p.TempC, p.Rpm);
            var hovered = _mouseInside && (s - _mouse).Length <= HitRadius;
            var r = hovered ? HandleRadius + 2 : HandleRadius;
            dc.DrawEllipse(hovered ? accentBrush : handleFill, handlePen, s, r, r);
        }

        // Axis numbers only appear while the pointer is over the chart, so the resting
        // state stays clean but the values are still there when you are editing.
        if (!_mouseInside) return;

        for (var rpm = RoundUpTo(MinRpm, rpmStep); rpm <= MaxRpm; rpm += rpmStep)
        {
            var y = ToScreen(MinTemp, rpm).Y;
            if (y <= area.Top + 6 || y >= area.Bottom - 6) continue;
            DrawText(dc, $"{rpm}", new Point(area.Left + 4, y - 14), 10, "#5A636E", dpi);
        }

        for (var t = RoundUpTo((int)MinTemp, 15); t <= MaxTemp; t += 15)
        {
            var x = ToScreen(t, MinRpm).X;
            if (x <= area.Left + 6 || x >= area.Right - 20) continue;
            DrawText(dc, $"{t}°", new Point(x + 4, area.Bottom - 15), 10, "#5A636E", dpi);
        }

        var hoverIndex = HitTest(_mouse);
        if (hoverIndex >= 0 && hoverIndex < points.Count)
        {
            var p = points[hoverIndex];
            var s = ToScreen(p.TempC, p.Rpm);
            DrawText(dc, $"{p.TempC:0}°C → {p.Rpm} rpm",
                new Point(Math.Min(s.X + 14, area.Right - 120), Math.Max(area.Top, s.Y - 24)),
                11, "#E6EAF0", dpi);
        }
    }

    private static int RoundUpTo(int value, int step) => (int)(Math.Ceiling(value / (double)step) * step);

    private static void DrawText(DrawingContext dc, string text, Point at, double size, string hexColor, double dpi)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor)!);
        brush.Freeze();
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono, Consolas"), size, brush, dpi);
        dc.DrawText(ft, at);
    }
}
