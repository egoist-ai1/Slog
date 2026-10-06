using Egoist.Voice.Services;
using Microsoft.Win32;
using Xunit;

namespace Egoist.Voice.Tests;

public sealed class AutostartServiceTests : IDisposable
{
    private const string Exe = @"C:\Program Files\Slog App\Egoist.Voice.exe";
    private readonly string _key = @"Software\SlogTests\" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Enable_writes_a_quoted_background_command_and_disable_removes_it()
    {
        Assert.False(AutostartService.IsEnabled(_key));
        Assert.True(AutostartService.SetEnabled(true, Exe, _key));
        Assert.True(AutostartService.IsEnabled(_key));
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal(AutostartService.CommandFor(Exe) + "", key!.GetValue(AutostartService.ValueName));
        Assert.True(AutostartService.SetEnabled(false, "ignored", _key));
        Assert.False(AutostartService.IsEnabled(_key));
    }

    [Fact]
    public void Disabling_when_nothing_is_registered_is_harmless() =>
        Assert.True(AutostartService.SetEnabled(false, "x", _key));

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\SlogTests", throwOnMissingSubKey: false);
}
