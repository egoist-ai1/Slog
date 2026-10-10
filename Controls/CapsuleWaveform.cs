using System.Windows;
using System.Windows.Media;
using Egoist.Voice.Services;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace Egoist.Voice.Controls;

/// <summary>A crisp lime spectrum: thin mirrored bars with falling peak ticks and a fine contour line (high contrast uses plain system-colour bars).</summary>
public sealed class CapsuleWaveform : FrameworkElement
{
    private readonly double[] _levels = new double[CapsuleWaveformProfile.BarCount];
    private readonly double[] _drawnLevels = new double[CapsuleWaveformProfile.BarCount];
    private bool _highContrast;
    internal int RedrawRequestCount { get; private set; }
    private const int Bars = 44;
    private readonly double[] _peaks = new double[Bars];
    private double _drift;
    // Время, накопленное с прошлой отрисовки: дрейф и падение пиков идут по секундам, а не по кадрам.
    private double _elapsedSinceRender;
    private bool _peaksFalling;
    private readonly System.Windows.Point[] _tops = new System.Windows.Point[Bars];
    private const double DriftPerSecond = 3.0;
    private const double PeakFallPixelsPerSecond = 33.0;

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
        _elapsedSinceRender = Math.Min(_elapsedSinceRender + deltaSeconds, 1.0);
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
        // Пики ещё опускаются: нужен кадр, даже если уровни уже осели.
        if (_peaksFalling)
        {
            RedrawRequestCount++;
            InvalidateVisual();
            return;
        }
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

    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(168, 255, 0)));
    private static readonly Brush TipBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 255, 140)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromArgb(215, 214, 255, 92)));
    private static readonly Pen ContourPen = CreatePen(System.Windows.Media.Color.FromArgb(120, 190, 255, 40), 0.9);
    private static readonly Pen AxisPen = CreatePen(System.Windows.Media.Color.FromArgb(70, 168, 255, 0), 0.8);

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    private static Pen CreatePen(System.Windows.Media.Color color, double thickness) =>
        Freeze(new Pen(new SolidColorBrush(color), thickness) { LineJoin = PenLineJoin.Round });

    /// <summary>Fifteen smoothed bands are interpolated across the bars; a slow phase adds fine structure.</summary>
    private double BarLevel(int bar)
    {
        var position = bar / (double)(Bars - 1) * (_drawnLevels.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, _drawnLevels.Length - 1);
        var blend = position - lower;
        var level = _drawnLevels[lower] * (1 - blend) + _drawnLevels[upper] * blend;
        var fine = 0.8 + 0.2 * Math.Sin(bar * 2.1 + _drift * 2.6) * Math.Cos(bar * 0.9 - _drift * 1.5);
        var edge = Math.Pow(Math.Sin(Math.PI * (bar + 0.5) / Bars), 0.55);
        return Math.Clamp(level * fine * edge, 0, 1);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var elapsed = _elapsedSinceRender;
        _elapsedSinceRender = 0;
        _drift += DriftPerSecond * elapsed;
        var peakFall = PeakFallPixelsPerSecond * elapsed;
        var peaksFalling = false;
        var cell = ActualWidth / Bars;
        var centre = ActualHeight / 2;
        var width = _highContrast ? Math.Min(4, cell * 0.6) : Math.Max(1.8, cell * 0.58);
        var contrast = _highContrast ? System.Windows.SystemColors.WindowTextBrush : BarBrush;
        var tops = _tops;
        var loudest = 0d;
        for (var bar = 0; bar < Bars; bar++)
        {
            var level = BarLevel(bar);
            loudest = Math.Max(loudest, level);
            var half = Math.Max(1.1, level * ActualHeight / 2);
            var x = cell * (bar + 0.5);
            tops[bar] = new System.Windows.Point(x, centre - half);
            _peaks[bar] = Math.Max(half, _peaks[bar] - peakFall);
            if (_peaks[bar] - half > 0.05) peaksFalling = true;
            drawingContext.DrawRoundedRectangle(contrast, null,
                new Rect(x - width / 2, centre - half, width, half * 2), width / 2, width / 2);
            if (_highContrast) continue;
            if (half > 3)
            {
                drawingContext.DrawRectangle(TipBrush, null, new Rect(x - width / 2, centre - half, width, Math.Min(2.2, half * 0.5)));
                drawingContext.DrawRectangle(TipBrush, null, new Rect(x - width / 2, centre + half - Math.Min(2.2, half * 0.5), width, Math.Min(2.2, half * 0.5)));
            }
            if (_peaks[bar] - half > 1.5)
            {
                var peak = _peaks[bar] + 2;
                drawingContext.DrawRectangle(PeakBrush, null, new Rect(x - width / 2, centre - peak - 1.2, width, 1.2));
                drawingContext.DrawRectangle(PeakBrush, null, new Rect(x - width / 2, centre + peak, width, 1.2));
            }
        }
        _peaksFalling = peaksFalling && !_highContrast;
        if (_highContrast) return;
        if (loudest < 0.2) drawingContext.DrawLine(AxisPen, new System.Windows.Point(cell * 0.5, centre), new System.Windows.Point(ActualWidth - cell * 0.5, centre));
        DrawContour(drawingContext, tops, centre);
    }

    /// <summary>Верхний и зеркальный контур одной геометрией: одна команда рисования вместо двух.</summary>
    private static void DrawContour(DrawingContext context, System.Windows.Point[] tops, double centre)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            foreach (var mirrored in new[] { false, true })
            {
                g.BeginFigure(Mirror(tops[0], centre, mirrored), false, false);
                for (var i = 1; i < tops.Length; i++)
                {
                    var previous = Mirror(tops[i - 1], centre, mirrored);
                    var current = Mirror(tops[i], centre, mirrored);
                    var middle = (previous.X + current.X) / 2;
                    g.BezierTo(new System.Windows.Point(middle, previous.Y), new System.Windows.Point(middle, current.Y), current, true, true);
                }
            }
        }
        geometry.Freeze();
        context.DrawGeometry(null, ContourPen, geometry);
    }

    private static System.Windows.Point Mirror(System.Windows.Point point, double centre, bool mirrored) =>
        mirrored ? new System.Windows.Point(point.X, 2 * centre - point.Y) : point;
}
