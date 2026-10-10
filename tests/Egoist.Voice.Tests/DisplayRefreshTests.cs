using System.Runtime.InteropServices;
using Egoist.Voice.Services;
using Xunit.Abstractions;

namespace Egoist.Voice.Tests;

public sealed class DisplayRefreshTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [Fact]
    public void Query_returns_a_plausible_frequency_or_zero_without_throwing()
    {
        var hertz = DisplayRefresh.Query(GetDesktopWindow());
        output.WriteLine($"refresh={hertz}");
        Assert.InRange(hertz, 0, 1000);
        Assert.Equal(0, DisplayRefresh.Query(0));
    }
}
