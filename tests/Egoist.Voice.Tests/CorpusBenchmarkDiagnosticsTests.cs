using System.IO;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class CorpusBenchmarkDiagnosticsTests
{
    [Fact]
    public void Shipped_quality_profile_selects_the_problem_buckets_without_private_data()
    {
        var root = RepositoryRoot();
        var corpus = Path.Combine(root, "tests", "corpus");
        var script = CorpusScript.Load(corpus);
        var profile = CorpusBenchmarkProfile.Load(
            Path.Combine(corpus, "quality-challenge-v1.json"),
            script);

        Assert.Equal("quality-challenge-v1", profile.Id);
        Assert.Equal(42, profile.SelectedIds.Count);
        Assert.Equal(25, profile.SelectedIds
            .Where(id => profile.BucketsFor(id).Contains("ru-en-entity", StringComparer.Ordinal))
            .Select(id => script.Lines.Single(line => line.Id == id).Entities.Count)
            .Sum());
        Assert.Equal(
            [
                "critical-phrase", "household-noise", "merged-vowel", "pure-ru",
                "quiet", "ru-en-entity", "short-onset", "sibilant-softness"
            ],
            profile.Buckets.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["critical-phrase", "merged-vowel", "quiet", "short-onset"],
            profile.BucketsFor("ru-quiet-phonetics/001"));
        Assert.Matches("^[0-9a-f]{64}$", profile.Fingerprint);
    }

    [Fact]
    public void Profile_rejects_unknown_ids_and_unmapped_fields()
    {
        var script = CorpusScript.Parse(
        [
            """{"kind":"schema","version":2,"privacy":"private-local-only"}""",
            """{"kind":"set","set":"ru-clean","title":"Обычная","hint":"","expectedCount":1}""",
            """{"kind":"line","id":"ru-clean/001","text":"Проверка"}"""
        ]);
        var directory = TemporaryDirectory();
        try
        {
            var unknown = Path.Combine(directory, "unknown.json");
            File.WriteAllText(
                unknown,
                """{"schema":"egoist.voice.corpus-profile/v1","id":"test","buckets":{"quiet":["ru-clean/002"]}}""");
            Assert.Throws<InvalidDataException>(() => CorpusBenchmarkProfile.Load(unknown, script));

            var unmapped = Path.Combine(directory, "unmapped.json");
            File.WriteAllText(
                unmapped,
                """{"schema":"egoist.voice.corpus-profile/v1","id":"test","buckets":{"quiet":["ru-clean/001"]},"privatePath":"C:\\Users\\Private"}""");
            Assert.Throws<InvalidDataException>(() => CorpusBenchmarkProfile.Load(unmapped, script));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Profile_fingerprint_accepts_only_selected_recordings_while_full_gate_stays_strict()
    {
        var script = CorpusScript.Parse(
        [
            """{"kind":"schema","version":2,"privacy":"private-local-only"}""",
            """{"kind":"set","set":"ru-clean","title":"Обычная","hint":"","expectedCount":2}""",
            """{"kind":"line","id":"ru-clean/001","text":"Первая"}""",
            """{"kind":"line","id":"ru-clean/002","text":"Вторая"}"""
        ]);
        var directory = TemporaryDirectory();
        try
        {
            var profilePath = Path.Combine(directory, "profile.json");
            File.WriteAllText(
                profilePath,
                """{"schema":"egoist.voice.corpus-profile/v1","id":"test","buckets":{"quiet":["ru-clean/001"]}}""");
            var profile = CorpusBenchmarkProfile.Load(profilePath, script);
            File.WriteAllText(
                Path.Combine(directory, CorpusBenchmark.ReferenceFileName),
                script.BuildReference(line => line.Id == "ru-clean/001"));
            var audio = Path.Combine(directory, "ru-clean", "001.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
            File.WriteAllBytes(audio, Enumerable.Repeat((byte)7, 64).ToArray());
            var references = CorpusBenchmark.LoadReferenceDocument(directory);

            var inventory = CorpusBenchmark.ValidateAndFingerprint(
                directory,
                script,
                references,
                profile.SelectedIds);

            Assert.Equal(1, inventory.Clips);
            Assert.Throws<InvalidDataException>(() =>
                CorpusBenchmark.ValidateAndFingerprint(directory, script, references));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Stage_attribution_distinguishes_gate_and_normalization()
    {
        var accepted = new SpeechActivitySnapshot(
            true,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(600),
            -12);
        var rejected = accepted with { HasSpeech = false, Rejection = SpeechRejection.TooQuiet };
        var samples = Enumerable.Repeat(0.05f, 16_000).ToArray();

        var gate = CorpusBenchmark.AnalyzeStages(
            "ну и уйди",
            samples,
            rejected,
            Observation("ну и уйди"),
            "ну и уйди",
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(1));
        Assert.Equal("SpeechGate", gate.AttributionCode);

        var normalization = CorpusBenchmark.AnalyzeStages(
            "ну и уйди",
            samples,
            accepted,
            Observation("ну и уйди"),
            "уйди",
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(1));
        Assert.Equal("Normalization", normalization.AttributionCode);
    }

    [Fact]
    public void Empty_decoded_audio_is_classified_before_any_native_decoder_is_needed()
    {
        var activity = new SpeechActivitySnapshot(
            false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            double.NegativeInfinity,
            SpeechRejection.NoAudio);

        Assert.Equal("CaptureNoAudio", CorpusBenchmark.ClassifyCapture([], activity));
        Assert.Equal("GateNoAudio", CorpusBenchmark.ClassifyGate(activity));
    }

    [Fact]
    public void Diagnostic_summary_contains_buckets_stage_percentiles_and_no_engine_selection_fields()
    {
        var timings = new BenchmarkEntryStageTimings(2, 100, 3, 184);
        var report = CorpusBenchmark.Summarize(
            "diagnostic-fixture",
            [
                new BenchmarkEntry(
                    "ru-quiet-phonetics/001", "ru-quiet-phonetics", "ну и уйди", "ну и уйди",
                    184, 100,
                    Buckets: ["quiet", "critical-phrase"],
                    CaptureCode: "CaptureVeryQuiet",
                    GateCode: "GateAccepted",
                    AttributionCode: "NoFailure",
                    PrimaryWordErrors: 0,
                    SelectedWordErrors: 0,
                    StageTimings: timings)
            ],
            context: new BenchmarkRunContext(
                new CorpusInventory(new string('a', 64), 1, 64, new string('b', 64)),
                new BenchmarkEnvironment("test", null, ".NET", "Windows", "X64", null, 8, []),
                CorpusBenchmark.CaptureParameters(),
                new BenchmarkResourceSnapshot(1, 1, 1, 1, 1),
                new BenchmarkResourceSnapshot(2, 2, 2, 2, 2),
                UiThreadStalls: new BenchmarkUiStallSummary(3, 1, 4, 4, 0)));

        var diagnostics = Assert.IsType<BenchmarkDiagnosticsSummary>(report.Diagnostics);
        Assert.Contains(diagnostics.StageTimings, stage => stage.Stage == "primary-decode" && stage.P95Ms == 100);
        Assert.Contains(diagnostics.Buckets, bucket => bucket.Bucket == "quiet" && bucket.Clips == 1);
        Assert.Equal(0, diagnostics.UiThreadStalls!.Over100Ms);
    }

    [Fact]
    public void Ui_stall_summary_uses_nearest_rank_and_counts_long_stalls()
    {
        var summary = UiThreadStallMonitor.Summarize(
            [
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(120)
            ]);

        Assert.Equal(4, summary.Samples);
        Assert.Equal(10, summary.P50Ms);
        Assert.Equal(120, summary.P95Ms);
        Assert.Equal(1, summary.Over100Ms);
    }

    private static BenchmarkObservation Observation(string text) => new(
        new TranscriptionResult(text, TimeSpan.FromMilliseconds(100)),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(180));

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "egoist-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Egoist.Voice.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
