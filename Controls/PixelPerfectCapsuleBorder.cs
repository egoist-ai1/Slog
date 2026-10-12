using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Egoist.Voice.Controls;

public sealed class PixelPerfectCapsuleBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(System.Windows.Media.Brush), typeof(PixelPerfectCapsuleBorder),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(System.Windows.Media.Brush), typeof(PixelPerfectCapsuleBorder),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(PixelPerfectCapsuleBorder),
        new FrameworkPropertyMetadata(26d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PhysicalStrokeProperty = DependencyProperty.Register(
        nameof(PhysicalStroke), typeof(double), typeof(PixelPerfectCapsuleBorder),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RasterScaleProperty = DependencyProperty.Register(
        nameof(RasterScale), typeof(int), typeof(PixelPerfectCapsuleBorder),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Brush Background { get => (System.Windows.Media.Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public System.Windows.Media.Brush BorderBrush { get => (System.Windows.Media.Brush)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public double PhysicalStroke { get => (double)GetValue(PhysicalStrokeProperty); set => SetValue(PhysicalStrokeProperty, value); }
    public int RasterScale { get => (int)GetValue(RasterScaleProperty); set => SetValue(RasterScaleProperty, value); }

    private CacheKey _cacheKey;
    private CroppedBitmap? _leftCap;
    private CroppedBitmap? _middle;
    private CroppedBitmap? _rightCap;
    private int _capPixels;

    public PixelPerfectCapsuleBorder()
    {
        // Set once, in the constructor. Assigning a dependency property from inside OnRender is
        // both wasteful and a good way to invalidate the very render that is in progress.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>Diagnostics for the render tests: how many times the chrome was rasterized.</summary>
    internal int RasterizationCount { get; private set; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = Math.Max(1, dpi.DpiScaleX);
        var capPixels = (int)Math.Ceiling(Math.Max(0, CornerRadius) * scaleX);
        var widthPixels = (int)Math.Round(ActualWidth * scaleX);

        // Width is deliberately NOT part of the cache key. The capsule animates its width for
        // 160-210 ms, and rasterizing a 4x chrome bitmap (about 1.7 MB, large object heap) on every
        // frame of that animation was the most expensive thing it did. Instead the chrome is
        // rasterized once at a canonical width into three slices: two rounded end caps and one
        // device-pixel-wide column of the straight middle, which is stretched to any width. The
        // curve on the caps keeps its 4x supersampled accuracy; the middle is uniform horizontally,
        // so stretching it loses nothing. All slice edges sit on whole device pixels (no seams).
        // Only a capsule narrower than two caps falls back to a full-width raster.
        if (widthPixels < 2 * capPixels + 1 || capPixels < 1)
        {
            DrawFullWidth(drawingContext, dpi);
            return;
        }

        var key = new CacheKey(
            ActualHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            CornerRadius,
            PhysicalStroke,
            RasterScale,
            Background,
            PhysicalStroke > 0 ? BorderBrush : System.Windows.Media.Brushes.Transparent);

        if (_leftCap is null || !_cacheKey.Equals(key))
        {
            RasterizeSlices(dpi, capPixels);
            _cacheKey = key;
            RasterizationCount++;
        }

        var capWidth = _capPixels / scaleX;
        var rightStart = (widthPixels - _capPixels) / scaleX;
        drawingContext.DrawImage(_leftCap!, new Rect(0, 0, capWidth, ActualHeight));
        if (rightStart > capWidth)
        {
            drawingContext.DrawImage(_middle!, new Rect(capWidth, 0, rightStart - capWidth, ActualHeight));
        }

        drawingContext.DrawImage(_rightCap!, new Rect(rightStart, 0, capWidth, ActualHeight));
    }

    private void DrawFullWidth(DrawingContext drawingContext, DpiScale dpi)
    {
        _leftCap = null;
        drawingContext.DrawImage(RasterizeChrome(dpi, ActualWidth), new Rect(0, 0, ActualWidth, ActualHeight));
        RasterizationCount++;
    }

    private void RasterizeSlices(DpiScale dpi, int capPixels)
    {
        var scaleX = Math.Max(1, dpi.DpiScaleX);
        var rasterScale = Math.Clamp(RasterScale, 1, 8);
        // Canonical width: two caps plus one device-pixel column of straight middle.
        var canonicalWidth = (2 * capPixels + 1) / scaleX;
        var bitmap = RasterizeChrome(dpi, canonicalWidth);
        var capRaster = capPixels * rasterScale;
        var height = bitmap.PixelHeight;
        _leftCap = new CroppedBitmap(bitmap, new Int32Rect(0, 0, capRaster, height));
        _middle = new CroppedBitmap(bitmap, new Int32Rect(capRaster, 0, rasterScale, height));
        _rightCap = new CroppedBitmap(bitmap, new Int32Rect(capRaster + rasterScale, 0, capRaster, height));
        _leftCap.Freeze();
        _middle.Freeze();
        _rightCap.Freeze();
        _capPixels = capPixels;
    }

    private BitmapSource RasterizeChrome(DpiScale dpi, double width)
    {
        var height = ActualHeight;
        var profile = CalculateRasterProfile(
            width,
            height,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            PhysicalStroke,
            RasterScale);
        var scaleX = profile.DpiScaleX;
        var scaleY = profile.DpiScaleY;
        var stroke = profile.StrokeDip;
        var rasterScale = profile.RasterScale;
        var inset = stroke / 2;
        var rect = new Rect(inset, inset, Math.Max(0, width - stroke), Math.Max(0, height - stroke));
        var radius = Math.Max(0, CornerRadius - inset);
        var pen = new System.Windows.Media.Pen(BorderBrush, stroke);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        // A one-device-pixel WPF arc is rasterized from only one sample per output
        // pixel. On a 52 px capsule that makes neighbouring curve pixels alternate
        // between bright and almost transparent even though the vector is correct.
        // Render the small chrome layer at 4x device resolution and downsample it;
        // child content remains native WPF and therefore stays sharp.
        var pixelWidth = profile.PixelWidth;
        var pixelHeight = profile.PixelHeight;
        var chrome = new DrawingVisual();
        using (var chromeContext = chrome.RenderOpen())
        {
            chromeContext.PushTransform(new ScaleTransform(
                scaleX * rasterScale,
                scaleY * rasterScale));
            chromeContext.DrawRoundedRectangle(Background, stroke > 0 ? pen : null, rect, radius, radius);
            chromeContext.Pop();
        }

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(chrome);
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    /// <summary>
    /// Everything the rasterized slices depend on (width is not among them). Brushes are compared
    /// by reference, which is exactly right here: the capsule swaps between a handful of frozen
    /// static brushes, so a reference change is a real change and equality never gives a false hit.
    /// </summary>
    private readonly record struct CacheKey(
        double Height,
        double DpiScaleX,
        double DpiScaleY,
        double CornerRadius,
        double PhysicalStroke,
        int RasterScale,
        System.Windows.Media.Brush? Background,
        System.Windows.Media.Brush? BorderBrush);

    internal static CapsuleRasterProfile CalculateRasterProfile(
        double width,
        double height,
        double dpiScaleX,
        double dpiScaleY,
        double physicalStroke,
        int rasterScale)
    {
        var scaleX = Math.Max(1, dpiScaleX);
        var scaleY = Math.Max(1, dpiScaleY);
        var normalizedStroke = Math.Clamp(physicalStroke, 0d, 3d);
        var normalizedRasterScale = Math.Clamp(rasterScale, 1, 8);
        return new CapsuleRasterProfile(
            scaleX,
            scaleY,
            normalizedStroke / scaleX,
            normalizedRasterScale,
            Math.Max(1, (int)Math.Ceiling(width * scaleX * normalizedRasterScale)),
            Math.Max(1, (int)Math.Ceiling(height * scaleY * normalizedRasterScale)));
    }
}

internal readonly record struct CapsuleRasterProfile(
    double DpiScaleX,
    double DpiScaleY,
    double StrokeDip,
    int RasterScale,
    int PixelWidth,
    int PixelHeight);
