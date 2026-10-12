using System.IO;
using System.Text.Json.Nodes;
using Egoist.Voice.Core;

namespace Egoist.Voice.Tests;

public sealed class CorpusBenchmarkCompatibilityTests
{
    [Fact]
    public void Legacy_v3_report_with_whisper_fields_loads_and_is_rejected_against_v4_pipeline()
    {
        var current = CurrentReport();
        var directory = Path.Combine(Path.GetTempPath(), "egoist-compat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var currentPath = Path.Combine(directory, "v4.json");
            CorpusBenchmark.Save(current, currentPath);
            Assert.Equal("RussianSpeechQualityService", CorpusBenchmark.Load(currentPath)!.Parameters!.Pipeline);

            // Фикстура v3: тот же отчёт, но со снятыми полями и прежним конвейером.
            var node = JsonNode.Parse(File.ReadAllText(currentPath))!.AsObject();
            node["schema"] = "egoist.voice.corpus-benchmark/v3";
            var parameters = node["parameters"]!.AsObject();
            parameters["pipeline"] = "HybridTranscriptionService";
            parameters["whisperThreads"] = 4;
            parameters["whisperRuntimePreference"] = "auto";
            parameters["whisperRuntimeLoaded"] = "Cuda";
            parameters["whisperLanguageDetection"] = true;
            parameters["whisperSampling"] = "beam-search/default-size";
            parameters["whisperNoContext"] = true;
            parameters["mixedLanguageMode"] = false;
            var entry = node["entries"]![0]!.AsObject();
            entry["fallbackTrigger"] = "None";
            entry["fallbackRan"] = false;
            entry["selectedEngine"] = "GigaAM";
            entry["fallbackWordErrors"] = 0;
            node["diagnostics"]!.AsObject()["fallbackRequestedClips"] = 0;
            var legacyPath = Path.Combine(directory, "v3.json");
            File.WriteAllText(legacyPath, node.ToJsonString());

            var legacy = CorpusBenchmark.Load(legacyPath);

            Assert.NotNull(legacy);
            Assert.Equal("HybridTranscriptionService", legacy.Parameters!.Pipeline);
            var breaches = CorpusBenchmark.CompareToBaseline(legacy, current);
            Assert.Contains(breaches, breach => breach.Contains("другом конвейере", StringComparison.Ordinal));
            Assert.Empty(CorpusBenchmark.CompareToBaseline(current, current));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void V4_report_keeps_transcript_text_out_of_the_file()
    {
        const string canary = "TRANSCRIPT-CANARY-V4";
        var report = CorpusBenchmark.Summarize(
            "v4-privacy",
            [new BenchmarkEntry("ru-clean/001", "ru-clean", canary, canary, 10, 10)]);
        var directory = Path.Combine(Path.GetTempPath(), "egoist-compat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.json");
            CorpusBenchmark.Save(report, path);
            var json = File.ReadAllText(path);

            Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
            Assert.Contains("corpus-benchmark/v4", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BenchmarkReport CurrentReport() => CorpusBenchmark.Summarize(
        "compat-fixture",
        [
            new BenchmarkEntry(
                "ru-clean/001", "ru-clean", "проверка связи", "проверка связи", 20, 10,
                CaptureCode: "CaptureUsable", GateCode: "GateAccepted", AttributionCode: "NoFailure",
                PrimaryWordErrors: 0, SelectedWordErrors: 0,
                StageTimings: new BenchmarkEntryStageTimings(1, 10, 1, 20))
        ],
        context: new BenchmarkRunContext(
            new CorpusInventory(new string('a', 64), 1, 64, new string('b', 64)),
            new BenchmarkEnvironment("test", null, ".NET", "Windows", "X64", null, 8, []),
            CorpusBenchmark.CaptureParameters(),
            new BenchmarkResourceSnapshot(1, 1, 1, 1, 1),
            new BenchmarkResourceSnapshot(2, 2, 2, 2, 2)));
}
