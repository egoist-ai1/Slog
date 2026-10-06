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

    private static readonly Brush FrontBrush = CreateRibbonBrush(1.0);
    private static readonly Brush MidBrush = CreateRibbonBrush(0.5);
    private static readonly Brush BackBrush = CreateRibbonBrush(0.22);
    private static readonly Pen CrestPen = CreateCrestPen();
    private double _drift;

    private static Brush CreateRibbonBrush(double opacity)
    {
        // Lime that fades to nothing at both ends so the ribbon dissolves into the capsule.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 168, 255, 0), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb((byte)(255 * opacity), 168, 255, 0), 0.22));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb((byte)(255 * opacity), 214, 255, 92), 0.5));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb((byte)(255 * opacity), 168, 255, 0), 0.78));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 168, 255, 0), 1));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateCrestPen()
    {
        var pen = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 236, 255, 170)), 1) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
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
        // Three layers of one ribbon: a wide soft echo, a body, and a bright crest. The layers
        // follow the same spectrum with a small phase lag, which is what reads as a flowing wave.
        DrawRibbon(drawingContext, BackBrush, null, 1.0, lag: 2.0);
        DrawRibbon(drawingContext, MidBrush, null, 0.88, lag: 1.0);
        DrawRibbon(drawingContext, FrontBrush, CrestPen, 0.7, lag: 0);
    }

    private void DrawRibbon(DrawingContext context, Brush fill, Pen? crest, double scale, double lag)
    {
        var count = _drawnLevels.Length;
        var centre = ActualHeight / 2;
        var cell = ActualWidth / count;
        var top = new System.Windows.Point[count + 2];
        var bottom = new System.Windows.Point[count + 2];
        for (var i = -1; i <= count; i++)
        {
            var index = Math.Clamp(i, 0, count - 1);
            var ripple = 1 + 0.12 * Math.Sin(_drift * 3 + index * 0.8 + lag);
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
