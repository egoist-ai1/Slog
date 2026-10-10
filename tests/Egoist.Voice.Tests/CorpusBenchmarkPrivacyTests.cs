using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class CorpusBenchmarkPrivacyTests
{
    [Fact]
    public void Saved_report_contains_metrics_but_not_reference_hypothesis_or_paths()
    {
        const string referenceCanary = "REFERENCE-CANARY-CONTENT";
        const string hypothesisCanary = "HYPOTHESIS-CANARY-CONTENT C:\\Users\\Private\\voice.wav";
        var report = CorpusBenchmark.Summarize(
            "privacy-test",
            [new BenchmarkEntry("ru-clean/001", "ru-clean", referenceCanary, hypothesisCanary, 12, 14)]);
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "report.json");
        try
        {
            CorpusBenchmark.Save(report, path);
            var json = File.ReadAllText(path);

            Assert.DoesNotContain(referenceCanary, json, StringComparison.Ordinal);
            Assert.DoesNotContain(hypothesisCanary, json, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Users", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("aggregate-only-no-transcript", json, StringComparison.Ordinal);
            Assert.Contains("egoist.voice.corpus-benchmark/v3", json, StringComparison.Ordinal);
            Assert.Contains("wordErrors", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Crash_progress_cursor_contains_only_stable_id_and_counts()
    {
        const string transcriptCanary = "PRIVATE-TRANSCRIPT-CANARY";
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "progress.json");
        try
        {
            CorpusBenchmark.SaveProgress(path, "public-proxy", "started", 117, 775, "tone/118");
            var json = File.ReadAllText(path);

            Assert.Contains("egoist.voice.corpus-benchmark-progress/v1", json, StringComparison.Ordinal);
            Assert.Contains("tone/118", json, StringComparison.Ordinal);
            Assert.Contains("117", json, StringComparison.Ordinal);
            Assert.DoesNotContain(transcriptCanary, json, StringComparison.Ordinal);
            Assert.DoesNotContain("text", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("audio", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Crash_progress_cursor_rejects_invalid_phase_or_id()
    {
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "progress.json");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                CorpusBenchmark.SaveProgress(path, "public-proxy", "running", 0, 1, "tone/001"));
            Assert.Throws<InvalidDataException>(() =>
                CorpusBenchmark.SaveProgress(path, "public-proxy", "started", 0, 1, "../private"));
            Assert.Throws<InvalidDataException>(() =>
                CorpusBenchmark.SaveProgress(path, "public-proxy", "complete", 0, 1, null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Raw_engine_candidates_are_reduced_to_counts_before_report_serialization()
    {
        const string referenceCanary = "REFERENCE-RAW-CANARY";
        const string primaryCanary = "PRIMARY-RAW-CANARY";
        const string fallbackCanary = "FALLBACK-RAW-CANARY";
        const string selectedCanary = "SELECTED-RAW-CANARY";
        var observation = new HybridTranscriptionObservation(
            new TranscriptionResult(selectedCanary, TimeSpan.FromMilliseconds(20)),
            new TranscriptionResult(primaryCanary, TimeSpan.FromMilliseconds(10)),
            new TranscriptionResult(fallbackCanary, TimeSpan.FromMilliseconds(20)),
            MixedSpeechTrigger.Requested,
            "Whisper",
            PrimaryFailed: false,
            FallbackFailed: false,
            FallbackRan: true,
            FallbackUnavailable: false,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(31));
        var diagnostics = CorpusBenchmark.AnalyzeStages(
            referenceCanary,
            Enumerable.Repeat(0.05f, 16_000).ToArray(),
            new SpeechActivitySnapshot(true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), -12),
            observation,
            selectedCanary,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
        var report = CorpusBenchmark.Summarize(
            "raw-privacy-test",
            [
                new BenchmarkEntry(
                    "ru-clean/001", "ru-clean", referenceCanary, selectedCanary, 32, 20,
                    CaptureCode: diagnostics.CaptureCode,
                    GateCode: diagnostics.GateCode,
                    AttributionCode: diagnostics.AttributionCode,
                    FallbackTrigger: diagnostics.FallbackTrigger,
                    FallbackRan: diagnostics.FallbackRan,
                    FallbackUnavailable: diagnostics.FallbackUnavailable,
                    SelectedEngine: diagnostics.SelectedEngine,
                    PrimaryWordErrors: diagnostics.PrimaryWordErrors,
                    FallbackWordErrors: diagnostics.FallbackWordErrors,
                    SelectedWordErrors: diagnostics.SelectedWordErrors,
                    StageTimings: diagnostics.StageTimings)
            ]);
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "report.json");
            CorpusBenchmark.Save(report, path);
            var json = File.ReadAllText(path);

            Assert.DoesNotContain(referenceCanary, json, StringComparison.Ordinal);
            Assert.DoesNotContain(primaryCanary, json, StringComparison.Ordinal);
            Assert.DoesNotContain(fallbackCanary, json, StringComparison.Ordinal);
            Assert.DoesNotContain(selectedCanary, json, StringComparison.Ordinal);
            Assert.Contains("primaryWordErrors", json, StringComparison.Ordinal);
            Assert.Contains("fallbackWordErrors", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Summary_reports_entity_split_command_punctuation_and_boundary_metrics()
    {
        var report = CorpusBenchmark.Summarize(
            "metric-test",
            [
                new BenchmarkEntry(
                    "translate-positive/001",
                    "translate-positive",
                    "Переведи на английский: Anthropic.",
                    "Переведи на английский: Anthropic.",
                    20,
                    25,
                    ExpectedEntities: ["Anthropic"],
                    TranslationCommandExpected: true),
                new BenchmarkEntry(
                    "translate-negative/001",
                    "translate-negative",
                    "Я закончил перевод вчера.",
                    "Я закончил перевод вчера.",
                    18,
                    22,
                    TranslationCommandExpected: false),
                new BenchmarkEntry(
                    "ru-en/001",
                    "ru-en",
                    "Anthropic готов.",
                    "Anth ropic готов.",
                    16,
                    20,
                    ExpectedEntities: ["Anthropic"]),
                new BenchmarkEntry(
                    "boundary-start/001",
                    "boundary-start",
                    "Шёпот слышен.",
                    "Шёпот слышен.",
                    15,
                    19,
                    Boundary: "start",
                    BoundaryTarget: "Шёпот")
            ]);

        Assert.Equal(2, report.EntitiesExpected);
        Assert.Equal(0.5, report.EntityAccuracy, precision: 6);
        Assert.Equal(1, report.SplitErrors);
        Assert.Equal(1, report.CommandPrecision, precision: 6);
        Assert.Equal(1, report.CommandRecall, precision: 6);
        Assert.Equal(1, report.BoundaryAccuracy, precision: 6);
        Assert.True(report.PunctuationF1 > 0.9);
    }

    [Fact]
    public void Repeated_fixture_summary_is_byte_stable_after_explicit_runtime_fields_are_fixed()
    {
        var entries = new[]
        {
            new BenchmarkEntry(
                "ru-clean/001",
                "ru-clean",
                "Проверка, один.",
                "Проверка, один.",
                12,
                15)
        };
        var first = CorpusBenchmark.Summarize("stable-fixture", entries) with
        {
            GeneratedUtc = DateTime.UnixEpoch
        };
        var second = CorpusBenchmark.Summarize("stable-fixture", entries) with
        {
            GeneratedUtc = DateTime.UnixEpoch
        };
        var directory = TemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(directory, "first.json");
            var secondPath = Path.Combine(directory, "second.json");
            CorpusBenchmark.Save(first, firstPath);
            CorpusBenchmark.Save(second, secondPath);

            Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Captured_parameters_pin_the_current_offline_hybrid_pipeline()
    {
        var parameters = CorpusBenchmark.CaptureParameters();

        Assert.Equal("HybridTranscriptionService", parameters.Pipeline);
        Assert.Equal(16_000, parameters.InputSampleRateHz);
        Assert.Equal(500, parameters.CapturePreRollMs);
        Assert.Equal(250, parameters.CaptureReleaseTailMs);
        Assert.True(parameters.GigaAmThreads > 0);
        Assert.False(parameters.GigaAmContextualBias);
        Assert.Null(parameters.GigaAmHotwordVersion);
        Assert.Null(parameters.GigaAmHotwordScore);
        Assert.True(parameters.WhisperThreads > 0);
        Assert.Equal("auto", parameters.WhisperRuntimePreference);
        Assert.Equal("not-loaded", parameters.WhisperRuntimeLoaded);
        Assert.True(parameters.WhisperLanguageDetection);
        Assert.True(parameters.WhisperNoContext);
        Assert.False(parameters.MixedLanguageMode);
        Assert.Equal(BuiltInVocabulary.Version, parameters.EntityCatalogVersion);
        Assert.Equal("target-and-utterance/v1", parameters.EntityProfilePolicy);
        Assert.False(parameters.ModelDownloadAllowed);

        var hotwords = CorpusBenchmark.CaptureParameters(enableContextualBias: true);
        Assert.False(hotwords.GigaAmContextualBias);
        Assert.Null(hotwords.GigaAmHotwordVersion);
        Assert.Null(hotwords.GigaAmHotwordScore);
    }

    [Fact]
    public void Captured_environment_hashes_the_managed_entry_assembly_not_the_stable_apphost()
    {
        var entryPath = Assembly.GetEntryAssembly()!.Location;
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entryPath)))
            .ToLowerInvariant();

        var environment = CorpusBenchmark.CaptureEnvironment([]);

        Assert.Equal(expected, environment.AppSha256);
    }

    [Fact]
    public void Complete_private_corpus_gets_a_path_independent_content_hash()
    {
        var script = CorpusScript.Parse(
        [
            """{"kind":"schema","version":2,"privacy":"private-local-only"}""",
            """{"kind":"set","set":"ru-clean","title":"Обычная","hint":"","expectedCount":1}""",
            """{"kind":"line","id":"ru-clean/001","text":"Проверка","tags":["clean"]}"""
        ]);
        var first = TemporaryDirectory();
        var second = TemporaryDirectory();
        try
        {
            PrepareCorpus(first, script, fill: 7);
            PrepareCorpus(second, script, fill: 7);

            var firstInventory = CorpusBenchmark.ValidateAndFingerprint(
                first, script, CorpusBenchmark.LoadReferenceDocument(first));
            var secondInventory = CorpusBenchmark.ValidateAndFingerprint(
                second, script, CorpusBenchmark.LoadReferenceDocument(second));

            Assert.Equal(firstInventory.Sha256, secondInventory.Sha256);
            Assert.Equal(1, firstInventory.Clips);
            Assert.Equal(64, firstInventory.AudioBytes);

            File.WriteAllBytes(Path.Combine(second, "ru-clean", "001.wav"), Enumerable.Repeat((byte)8, 64).ToArray());
            var changed = CorpusBenchmark.ValidateAndFingerprint(
                second, script, CorpusBenchmark.LoadReferenceDocument(second));
            Assert.NotEqual(firstInventory.Sha256, changed.Sha256);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void Reference_loader_rejects_audio_path_escape()
    {
        var directory = TemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, CorpusBenchmark.ReferenceFileName),
                """{"id":"ru-clean/001","audio":"../private.wav","text":"Проверка","tags":["clean"]}""");

            var exception = Assert.Throws<InvalidDataException>(() =>
                CorpusBenchmark.LoadReferenceDocument(directory));
            Assert.Contains("audio path", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void PrepareCorpus(string directory, CorpusScript script, byte fill)
    {
        File.WriteAllText(
            Path.Combine(directory, CorpusBenchmark.ReferenceFileName),
            script.BuildReference(_ => true),
            new UTF8Encoding(true));
        var audio = Path.Combine(directory, "ru-clean", "001.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        File.WriteAllBytes(audio, Enumerable.Repeat(fill, 64).ToArray());
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "egoist-corpus-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
