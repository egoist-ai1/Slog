using Egoist.Voice.Services;
using System.Windows.Input;

namespace Egoist.Voice.Tests;

public sealed class PushToTalkTests
{
    [Fact]
    public void CoordinatorStartsOnFirstSourceAndStopsAfterLastSource()
    {
        var coordinator = new PushToTalkCoordinator();

        Assert.True(coordinator.Press(PushToTalkSource.Keyboard));
        Assert.False(coordinator.Press(PushToTalkSource.Mouse));
        Assert.False(coordinator.Release(PushToTalkSource.Keyboard));
        Assert.True(coordinator.Release(PushToTalkSource.Mouse));
    }

    [Fact]
    public void CoordinatorIgnoresDuplicatePressAndRelease()
    {
        var coordinator = new PushToTalkCoordinator();

        Assert.True(coordinator.Press(PushToTalkSource.Mouse));
        Assert.False(coordinator.Press(PushToTalkSource.Mouse));
        Assert.True(coordinator.Release(PushToTalkSource.Mouse));
        Assert.False(coordinator.Release(PushToTalkSource.Mouse));
    }

    [Fact]
    public void PausedCoordinatorSuppressesBothActivationSourcesAndClearsHeldState()
    {
        var coordinator = new PushToTalkCoordinator();
        Assert.True(coordinator.Press(PushToTalkSource.Keyboard));

        coordinator.SetPaused(true);

        Assert.False(coordinator.Press(PushToTalkSource.Keyboard));
        Assert.False(coordinator.Press(PushToTalkSource.Mouse));
        Assert.False(coordinator.Release(PushToTalkSource.Keyboard));

        coordinator.SetPaused(false);
        Assert.True(coordinator.Press(PushToTalkSource.Mouse));
    }

    [Theory]
    [InlineData("dota2.exe", null, true)]
    [InlineData("unknown.exe", @"D:\SteamLibrary\steamapps\common\Some Game\game.exe", true)]
    [InlineData("FC26.exe", null, true)]
    [InlineData("FC25", @"C:\Program Files\EA Games\EA SPORTS FC 25\FC25.exe", true)]
    [InlineData("game.exe", @"D:\EA Games\Some Title\game.exe", true)]
    [InlineData("fcpdf.exe", null, false)]
    [InlineData("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData("Telegram.exe", null, false)]
    public void GamePolicyUsesKnownNamesAndInstallLocations(string process, string? path, bool expected)
    {
        Assert.Equal(expected, GameForegroundPolicy.IsGame(process, path));
    }

    [Fact]
    public void MouseDataDecoderRecognizesXButton1HighWord()
    {
        Assert.Equal((ushort)1, MousePushToTalkService.HighWord(0x0001_0000));
        Assert.Equal((ushort)2, MousePushToTalkService.HighWord(0x0002_0000));
    }

    [Theory]
    [InlineData(0x0001_0000u, MouseSideButton.Mouse4, true)]
    [InlineData(0x0002_0000u, MouseSideButton.Mouse5, true)]
    [InlineData(0x0001_0000u, MouseSideButton.Mouse5, false)]
    [InlineData(0x0002_0000u, MouseSideButton.Mouse4, false)]
    public void MouseDataDecoderMatchesConfiguredButton(uint data, MouseSideButton button, bool expected)
    {
        Assert.Equal(expected, MousePushToTalkService.MatchesButton(data, button));
    }

    [Fact]
    public void DefaultActivationUsesMouse5WithKeyboardFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "egoist-voice-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "activation.json");
        try
        {
            var settings = new ActivationSettingsService(path);

            var loaded = settings.Load();
            Assert.Equal(ActivationBinding.Mouse5AndKeyboard, loaded.Binding);
            Assert.Equal(MouseSideButton.Mouse5, ActivationBindingInfo.MouseButton(loaded.Binding));
            Assert.True(ActivationBindingInfo.UsesKeyboard(loaded.Binding));
            Assert.Equal(KeyboardShortcut.Default, ActivationBindingInfo.Keyboard(loaded));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(ActivationBinding.Mouse5)]
    [InlineData(ActivationBinding.Mouse4)]
    [InlineData(ActivationBinding.Keyboard)]
    [InlineData(ActivationBinding.Mouse5AndKeyboard)]
    public void ActivationSettingsRoundTrip(ActivationBinding binding)
    {
        var root = Path.Combine(Path.GetTempPath(), "egoist-voice-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "activation.json");
        try
        {
            var settings = new ActivationSettingsService(path);
            settings.Save(new ActivationConfiguration(binding));
            Assert.Equal(binding, settings.Load().Binding);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CustomKeyboardShortcutRoundTripsAndKeepsReadableName()
    {
        var root = Path.Combine(Path.GetTempPath(), "egoist-voice-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "activation.json");
        try
        {
            var shortcut = new KeyboardShortcut(
                HotkeyModifiers.Control | HotkeyModifiers.Shift,
                0x56);
            var settings = new ActivationSettingsService(path);
            settings.Save(new ActivationConfiguration(ActivationBinding.CustomKeyboard, shortcut));

            var loaded = settings.Load();

            Assert.Equal(ActivationBinding.CustomKeyboard, loaded.Binding);
            Assert.Equal(shortcut, loaded.CustomShortcut);
            Assert.Equal("Ctrl + Shift + V", ActivationBindingInfo.DisplayName(loaded));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Version150ActivationJsonLoadsWithoutMigration()
    {
        var root = Path.Combine(Path.GetTempPath(), "egoist-voice-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "activation.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{\"Binding\":2}");

            var loaded = new ActivationSettingsService(path).Load();

            Assert.Equal(ActivationBinding.Mouse4, loaded.Binding);
            Assert.Null(loaded.CustomShortcut);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidCustomShortcutFallsBackWithoutBreakingStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "egoist-voice-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "activation.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "{\"Binding\":4,\"CustomShortcut\":{\"Modifiers\":2,\"VirtualKey\":17}}");

            var loaded = new ActivationSettingsService(path).Load();

            Assert.Equal(ActivationConfiguration.Default, loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeModifierMappingSupportsCompleteCustomChord()
    {
        var modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt |
                        HotkeyModifiers.Shift | HotkeyModifiers.Windows;

        Assert.Equal(0x000Fu, GlobalHotkeyService.ToNativeModifiers(modifiers));
    }

    [Fact]
    public void KeyboardCaptureConvertsWpfKeyAndModifiers()
    {
        var shortcut = KeyboardShortcut.FromKey(
            Key.V,
            ModifierKeys.Control | ModifierKeys.Shift);

        Assert.True(shortcut.IsValid);
        Assert.Equal("Ctrl + Shift + V", shortcut.DisplayName);
    }

    [Theory]
    [InlineData(0x56, false)]
    [InlineData(0x77, true)]
    public void StandaloneKeyboardActivationRejectsTypingButAllowsFunctionKeys(int virtualKey, bool expected)
    {
        Assert.Equal(expected, new KeyboardShortcut(HotkeyModifiers.None, virtualKey).IsValid);
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(false, false, false, true, false)]
    public void CapsuleHidePolicyHandlesProcessingCancellation(
        bool requested,
        bool recording,
        bool processing,
        bool forced,
        bool expected)
    {
        Assert.Equal(expected, CapsuleHidePolicy.CanComplete(requested, recording, processing, forced));
    }
}
