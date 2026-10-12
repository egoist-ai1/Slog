using System.IO;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class DictationSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "EgoistVoiceTests",
        Guid.NewGuid().ToString("N"));

    private DictationSettingsService Service => new(_root);

    [Fact]
    public void Defaults_are_returned_when_nothing_was_ever_saved()
    {
        var settings = Service.Load();

        Assert.True(settings.ApplyDictionary);
        Assert.True(settings.ApplyVoiceCommands);
        Assert.True(settings.RestoreClipboard);
        Assert.True(settings.SaveRecentRecordings);
        Assert.True(settings.DesktopNotifications);
        Assert.Equal(AppTheme.System, settings.Theme);

        // Number normalization is the one step that rewrites text the user did not ask to rewrite,
        // so it has to be opt-in.
        Assert.False(settings.ApplyNumberNormalization);
        Assert.False(settings.SoundFeedback);
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var service = Service;
        service.Save(DictationSettings.Default with
        {
            ApplyNumberNormalization = true,
            SoundVolume = 0.75,
            CaptureDeviceId = "endpoint-stable-id",
            IsCapturePaused = true,
            SaveRecentRecordings = false,
            DesktopNotifications = false,
            Theme = AppTheme.Light
        });

        var loaded = service.Load();

        Assert.True(loaded.ApplyNumberNormalization);
        Assert.Equal(0.75, loaded.SoundVolume);
        Assert.Equal("endpoint-stable-id", loaded.CaptureDeviceId);
        Assert.True(loaded.IsCapturePaused);
        Assert.False(loaded.SaveRecentRecordings);
        Assert.False(loaded.DesktopNotifications);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        var json = File.ReadAllText(service.SettingsPath);
        Assert.Contains("\"captureDeviceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isPaused\"", json, StringComparison.Ordinal);
        Assert.Contains("\"saveRecentRecordings\"", json, StringComparison.Ordinal);
        Assert.Contains("\"desktopNotifications\"", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"Light\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"capturePaused\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_instead_of_crashing()
    {
        var service = Service;
        Directory.CreateDirectory(_root);
        File.WriteAllText(service.SettingsPath, "{ this is not json");

        Assert.Equal(DictationSettings.Default, service.Load());
    }

    [Fact]
    public void The_dictionary_template_is_valid_and_immediately_useful()
    {
        var service = Service;
        service.EnsureDictionaryTemplate();

        var dictionary = service.LoadDictionary();

        Assert.True(dictionary.Count >= 4);
        Assert.Equal("Открой Python.", dictionary.Apply("Открой питон."));
        Assert.Equal("Разверни Kubernetes.", dictionary.Apply("Разверни кубернетес."));
    }

    [Fact]
    public void The_template_is_not_overwritten_once_the_user_has_edited_it()
    {
        var service = Service;
        service.EnsureDictionaryTemplate();
        File.WriteAllText(service.DictionaryPath,
            """{ "terms": [ { "spoken": ["моё"], "written": "MINE" } ] }""");

        service.EnsureDictionaryTemplate();

        Assert.Equal("MINE", service.LoadDictionary().Apply("моё"));
    }

    [Fact]
    public void Without_a_file_the_built_in_vocabulary_is_still_in_effect()
    {
        // The whole point of shipping a vocabulary: a fresh installation already knows how to
        // spell the terms, without the user first discovering a JSON file.
        Assert.Contains("GitHub", Service.LoadDictionary().Apply("открой гитхаб"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_dictionary_falls_back_to_the_built_in_one()
    {
        var service = Service;
        Directory.CreateDirectory(_root);
        File.WriteAllText(service.DictionaryPath, "not json at all");

        // Falling back to nothing would silently downgrade recognition quality on a typo in a
        // config file, which is exactly the kind of failure nobody connects to its cause.
        Assert.Contains("Docker", service.LoadDictionary().Apply("запусти докер"), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_translate_into_post_processing_options()
    {
        var options = (DictationSettings.Default with { ApplyNumberNormalization = true })
            .ToPostProcessingOptions();

        Assert.True(options.ApplyDictionary);
        Assert.True(options.ApplyNumberNormalization);
    }

    [Fact]
    public void Speech_punctuation_is_enabled_by_default()
    {
        Assert.True(DictationSettings.Default.FormatSpeechPunctuation);
        Assert.True(Service.Load().FormatSpeechPunctuation);
    }

    [Fact]
    public void Legacy_settings_without_punctuation_field_keep_it_enabled()
    {
        var service = Service;
        Directory.CreateDirectory(_root);
        File.WriteAllText(service.SettingsPath,
            """{ "isPaused": true }""");

        var loaded = service.Load();

        Assert.True(loaded.FormatSpeechPunctuation);
        Assert.True(loaded.IsCapturePaused);
    }

    [Fact]
    public void Removed_engine_keys_in_old_settings_are_ignored_and_dropped_on_save()
    {
        var service = Service;
        Directory.CreateDirectory(_root);
        File.WriteAllText(service.SettingsPath,
            """{ "mixedLanguageMode": true, "directGigaamFastMode": false, "isPaused": true, "soundVolume": 0.5 }""");

        var loaded = service.Load();

        Assert.True(loaded.IsCapturePaused);
        Assert.Equal(0.5, loaded.SoundVolume);

        service.Save(loaded);
        var json = File.ReadAllText(service.SettingsPath);
        Assert.DoesNotContain("mixedLanguageMode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("directGigaamFastMode", json, StringComparison.Ordinal);
        Assert.Equal(0.5, service.Load().SoundVolume);
    }

    [Fact]
    public void Disabled_punctuation_round_trips_with_exact_json_key()
    {
        var service = Service;
        service.Save(DictationSettings.Default with { FormatSpeechPunctuation = false });

        Assert.False(service.Load().FormatSpeechPunctuation);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(service.SettingsPath));
        Assert.False(document.RootElement.GetProperty("formatSpeechPunctuation").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("FormatSpeechPunctuation", out _));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory left behind is not worth failing a test over.
        }
    }
}
