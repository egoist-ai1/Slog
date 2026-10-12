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

    [Fact]
    public void EnsureEnabled_fills_a_missing_entry_only_when_wanted()
    {
        Assert.False(AutostartService.EnsureEnabled(false, Exe, _key));
        Assert.False(AutostartService.IsEnabled(_key));
        Assert.True(AutostartService.EnsureEnabled(true, Exe, _key));
        Assert.True(AutostartService.IsEnabled(_key));
    }

    [Fact]
    public void EnsureEnabled_keeps_an_existing_entry_and_ignores_development_builds()
    {
        Assert.True(AutostartService.SetEnabled(true, Exe, _key));
        Assert.False(AutostartService.EnsureEnabled(true, @"D:\other\Egoist.Voice.exe", _key));
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal(AutostartService.CommandFor(Exe), key!.GetValue(AutostartService.ValueName));

        Assert.True(AutostartService.SetEnabled(false, Exe, _key));
        Assert.False(AutostartService.EnsureEnabled(true, @"C:\src\bin\Release\net8.0-windows\Egoist.Voice.exe", _key));
        Assert.False(AutostartService.IsEnabled(_key));
    }

    [Fact]
    public void Autostart_is_on_by_default_in_settings() => Assert.True(DictationSettings.Default.Autostart);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\SlogTests", throwOnMissingSubKey: false);
}
