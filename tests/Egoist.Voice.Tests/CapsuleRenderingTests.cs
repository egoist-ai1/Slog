using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Egoist.Voice.Controls;

namespace Egoist.Voice.Tests;

public sealed class CapsuleRenderingTests
{
    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(2.00)]
    public void SupersampledStrokeKeepsTheSamePhysicalWeightAtEveryDpi(double dpiScale)
    {
        var profile = PixelPerfectCapsuleBorder.CalculateRasterProfile(
            218,
            48,
            dpiScale,
            dpiScale,
            1.6,
            4);

        Assert.Equal(1.6, profile.StrokeDip * dpiScale, precision: 8);
        Assert.Equal((int)Math.Ceiling(218 * dpiScale * 4), profile.PixelWidth);
        Assert.Equal((int)Math.Ceiling(48 * dpiScale * 4), profile.PixelHeight);
        Assert.Equal(4, profile.RasterScale);
    }

    [Fact]
    public void Width_animation_rasterizes_the_chrome_at_most_once() => RunSta(() =>
    {
        var background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x10));
        background.Freeze();
        var border = new PixelPerfectCapsuleBorder
        {
            Background = background,
            BorderBrush = Brushes.White,
            CornerRadius = 26,
            PhysicalStroke = 1,
            Height = 52
        };

        foreach (var width in new double[] { 120, 133, 147.5, 160, 182, 199, 210 })
        {
            border.Width = width;
            border.Measure(new Size(400, 100));
            border.Arrange(new Rect(0, 0, width, 52));
            var bitmap = new RenderTargetBitmap(260, 60, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(border);
        }

        Assert.InRange(border.RasterizationCount, 1, 1);
    });

    [Fact]
    public void Sliced_chrome_matches_the_full_width_render_in_the_middle_and_at_the_edges() => RunSta(() =>
    {
        var border = new PixelPerfectCapsuleBorder
        {
            Background = Brushes.DimGray,
            BorderBrush = Brushes.White,
            CornerRadius = 26,
            PhysicalStroke = 1,
            Height = 52,
            Width = 180
        };
        border.Measure(new Size(300, 60));
        border.Arrange(new Rect(0, 0, 180, 52));
        var bitmap = new RenderTargetBitmap(180, 52, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(border);
        var pixels = new byte[180 * 52 * 4];
        bitmap.CopyPixels(pixels, 180 * 4, 0);

        byte Alpha(int x, int y) => pixels[(y * 180 + x) * 4 + 3];
        Assert.Equal(255, Alpha(90, 26));      // fill in the middle
        Assert.Equal(255, Alpha(90, 0));       // top border row of the straight section
        Assert.Equal(255, Alpha(40, 0));       // seam between the cap and the stretched middle
        Assert.Equal(255, Alpha(140, 0));
        Assert.Equal(0, Alpha(0, 0));          // outside the rounded corner
        Assert.Equal(0, Alpha(179, 51));
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF test dispatcher timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
