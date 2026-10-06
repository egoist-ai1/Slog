using System.Windows;
using System.Windows.Media;
using Egoist.Voice.Services;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace Egoist.Voice.Controls;

/// <summary>A flowing three-layer lime ribbon driven by the voice spectrum (high contrast falls back to bars).</summary>
public sealed class CapsuleWaveform : FrameworkElement
{
    private readonly double[] _levels = new double[CapsuleWaveformProfile.BarCount];
    private readonly double[] _drawnLevels = new double[CapsuleWaveformProfile.BarCount];
    private bool _highContrast;
    internal int RedrawRequestCount { get; private set; }

    public CapsuleWaveform()
    {
        Array.Fill(_levels, CapsuleWaveformProfile.MinimumScale);
        Array.Fill(_drawnLevels, CapsuleWaveformProfile.MinimumScale);
        Height = CapsuleWaveformProfile.BarHeight;
        IsHitTestVisible = false;
    }

    public bool HighContrast
    {
        get => _highContrast;
        set
        {
            if (_highContrast == value) return;
            _highContrast = value;
            InvalidateVisual();
        }
    }

    public void SetUniformScale(double scale)
    {
        Array.Fill(_levels, Math.Clamp(scale, CapsuleWaveformProfile.MinimumScale, 1));
        RequestRedrawIfChanged();
    }

    public void Advance(double level, double phase, double deltaSeconds, bool reducedMotion,
        double bass = 0, double mid = 0, double treble = 0, VoiceSpectrum spectrum = default)
    {
        for (var index = 0; index < _levels.Length; index++)
        {
            var target = CapsuleWaveformProfile.TargetScale(
                index, _levels.Length, level, phase, reducedMotion, bass, mid, treble, spectrum);
            var frameTime = reducedMotion ? deltaSeconds * 0.35 : deltaSeconds;
            _levels[index] = CapsuleWaveformProfile.SmoothLevel(_levels[index], target, frameTime);
        }
        RequestRedrawIfChanged();
    }

    private void RequestRedrawIfChanged()
    {
        // Sub-pixel settling and silence do not need a new retained drawing on every tick.
        var height = ActualHeight > 0 ? ActualHeight : CapsuleWaveformProfile.BarHeight;
        for (var index = 0; index < _levels.Length; index++)
        {
            if (Math.Abs(_levels[index] - _drawnLevels[index]) * height < 0.15) continue;
            Array.Copy(_levels, _drawnLevels, _levels.Length);
            RedrawRequestCount++;
            InvalidateVisual();
            return;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? CapsuleWaveformProfile.PreferredWidth : availableSize.Width,
        CapsuleWaveformProfile.BarHeight);

    private static readonly Brush BackBrush = CreateHorizontalFade(0.20);
    private static readonly Brush MidBrush = CreateHorizontalFade(0.55);
    private static readonly Brush FrontBrush = CreateBodyBrush();
    private static readonly Pen CoreHalo = CreatePen(System.Windows.Media.Color.FromArgb(70, 214, 255, 92), 3.2);
    private static readonly Pen CoreLine = CreatePen(System.Windows.Media.Color.FromArgb(235, 244, 255, 190), 1.0);
    private double _drift;

    private static Pen CreatePen(System.Windows.Media.Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateHorizontalFade(double opacity)
    {
        var a = (byte)(255 * opacity);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 168, 255, 0), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a, 168, 255, 0), 0.25));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a, 190, 255, 40), 0.5));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a, 168, 255, 0), 0.75));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 168, 255, 0), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Solid body: deep lime at the rims, hot lime-white at the spine, like lit glass.</summary>
    private static Brush CreateBodyBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(120, 205, 0), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(176, 255, 20), 0.34));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(226, 255, 110), 0.5));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(176, 255, 20), 0.66));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(120, 205, 0), 1));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_highContrast)
        {
            RenderBars(drawingContext);
            return;
        }
        _drift += 0.05;
        // Echo, body, spine: the echo layers lag the body so the shape flows rather than pulses.
        DrawRibbon(drawingContext, BackBrush, null, 1.08, lag: 2.2, tailFade: true);
        DrawRibbon(drawingContext, MidBrush, null, 0.92, lag: 1.1, tailFade: true);
        DrawRibbon(drawingContext, FrontBrush, null, 0.74, lag: 0, tailFade: false);
        DrawSweep(drawingContext, 0.74);
        DrawSpine(drawingContext);
    }

    private void DrawSweep(DrawingContext context, double scale)
    {
        // A soft light travelling along the body: the part that makes it read as a lit object.
        var position = (_drift * 0.11) % 1.6 - 0.3;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), Math.Clamp(position - 0.16, 0, 1)));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(120, 255, 255, 225), Math.Clamp(position, 0, 1)));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), Math.Clamp(position + 0.16, 0, 1)));
        brush.Freeze();
        DrawRibbon(context, brush, null, scale, lag: 0, tailFade: false);
    }

    private void DrawSpine(DrawingContext context)
    {
        var centre = ActualHeight / 2;
        var cell = ActualWidth / _drawnLevels.Length;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(cell * 0.5, centre), false, false);
            g.LineTo(new Point(ActualWidth - cell * 0.5, centre), true, false);
        }
        geometry.Freeze();
        var level = 0d;
        foreach (var value in _drawnLevels) level = Math.Max(level, value);
        // The spine only shows through when the body is thin (quiet), which keeps the idle state crisp.
        if (level < 0.35)
        {
            context.DrawGeometry(null, CoreHalo, geometry);
            context.DrawGeometry(null, CoreLine, geometry);
        }
    }

    private void DrawRibbon(DrawingContext context, Brush fill, Pen? crest, double scale, double lag, bool tailFade)
    {
        var count = _drawnLevels.Length;
        var centre = ActualHeight / 2;
        var cell = ActualWidth / count;
        var top = new System.Windows.Point[count + 2];
        var bottom = new System.Windows.Point[count + 2];
        for (var i = -1; i <= count; i++)
        {
            var index = Math.Clamp(i, 0, count - 1);
            var ripple = 1 + 0.035 * Math.Sin(_drift * 2 + index * 0.6 + lag);
            var half = Math.Max(1, _drawnLevels[index] * scale * ripple * ActualHeight / 2);
            var edge = i < 0 || i >= count ? 0.2 : 1;
            var x = cell * (i + 0.5);
            top[i + 1] = new System.Windows.Point(x, centre - half * edge);
            bottom[i + 1] = new System.Windows.Point(x, centre + half * edge);
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(top[0], true, true);
            AddSmooth(g, top, forward: true);
            AddSmooth(g, bottom, forward: false);
        }
        geometry.Freeze();
        context.DrawGeometry(fill, crest, geometry);
    }

    private static void AddSmooth(StreamGeometryContext g, System.Windows.Point[] pts, bool forward)
    {
        // Catmull-Rom through the points, expressed as cubic Béziers.
        var n = pts.Length;
        for (var step = 0; step < n - 1; step++)
        {
            var a = forward ? step : n - 1 - step;
            var b = forward ? step + 1 : n - 2 - step;
            var prev = pts[Math.Clamp(forward ? a - 1 : a + 1, 0, n - 1)];
            var next = pts[Math.Clamp(forward ? b + 1 : b - 1, 0, n - 1)];
            var c1 = new System.Windows.Point(pts[a].X + (pts[b].X - prev.X) / 6, pts[a].Y + (pts[b].Y - prev.Y) / 6);
            var c2 = new System.Windows.Point(pts[b].X - (next.X - pts[a].X) / 6, pts[b].Y - (next.Y - pts[a].Y) / 6);
            g.BezierTo(c1, c2, pts[b], true, true);
        }
        if (!forward) g.LineTo(pts[0], false, false);
    }

    private void RenderBars(DrawingContext drawingContext)
    {
        var cell = ActualWidth / _levels.Length;
        var brush = System.Windows.SystemColors.WindowTextBrush;
        for (var index = 0; index < _levels.Length; index++)
        {
            var barWidth = Math.Min(5, cell * 0.58) * (0.4 + 0.6 * CapsuleWaveformProfile.EdgeEnvelope(index, _levels.Length));
            var height = Math.Clamp(_levels[index] * ActualHeight, barWidth, ActualHeight);
            drawingContext.DrawRoundedRectangle(brush, null,
                new Rect(cell * (index + 0.5) - barWidth / 2, (ActualHeight - height) / 2, barWidth, height),
                barWidth / 2, barWidth / 2);
        }
    }
}
