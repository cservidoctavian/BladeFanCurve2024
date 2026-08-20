using System.Windows;
using System.Windows.Media;
using BladeFanCurve.Hardware;

namespace BladeFanCurve.UI;

/// <summary>
/// Draws the 6 x 16 lighting matrix as a grid of keys. Purely a viewer: it is handed
/// frames and paints them, so the same renderer feeds both the keyboard and the window
/// and what you see is what the hardware was sent.
/// </summary>
public sealed class KeyboardPreview : FrameworkElement
{
    private RgbColor[,] _frame = new RgbColor[RazerChroma.Rows, RazerChroma.Columns];
    private readonly Pen _keyOutline;

    public KeyboardPreview()
    {
        _keyOutline = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        _keyOutline.Freeze();
        MinHeight = 132;
    }

    public void SetFrame(RgbColor[,] frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    public void Clear() => SetFrame(new RgbColor[RazerChroma.Rows, RazerChroma.Columns]);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        const double gap = 3;
        const double pad = 6;

        var cellW = (width - pad * 2 - gap * (RazerChroma.Columns - 1)) / RazerChroma.Columns;
        var cellH = (height - pad * 2 - gap * (RazerChroma.Rows - 1)) / RazerChroma.Rows;
        if (cellW <= 0 || cellH <= 0) return;

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0D)), null,
            new Rect(0, 0, width, height), 10, 10);

        for (var r = 0; r < RazerChroma.Rows; r++)
        for (var c = 0; c < RazerChroma.Columns; c++)
        {
            var colour = _frame[r, c];
            var rect = new Rect(
                pad + c * (cellW + gap),
                pad + r * (cellH + gap),
                cellW, cellH);

            // An unlit key still needs to read as a key, so it gets a faint body.
            var isDark = colour is { R: < 8, G: < 8, B: < 8 };
            var brush = isDark
                ? new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C))
                : new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
            brush.Freeze();

            dc.DrawRoundedRectangle(brush, _keyOutline, rect, 3, 3);

            // A brighter key spills a little light, which sells it as illuminated
            // rather than merely coloured.
            if (isDark) continue;

            var glow = new SolidColorBrush(Color.FromArgb(38, colour.R, colour.G, colour.B));
            glow.Freeze();
            dc.DrawRoundedRectangle(glow, null,
                Rect.Inflate(rect, gap * 0.9, gap * 0.9), 5, 5);
        }
    }
}
