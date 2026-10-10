using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Egoist.Voice.Services;
using Microsoft.Win32;

namespace Egoist.Voice.Core;

public sealed record CorpusReferenceManifest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("privacy")] string Privacy,
    [property: JsonPropertyName("scriptSha256")] string ScriptSha256);

/// <summary>One recorded clip and its hand-checked reference transcript.</summary>
public sealed record CorpusEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("audio")] string Audio,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("entities")] IReadOnlyList<string>? Entities = null,
    [property: JsonPropertyName("translationCommand")] bool? TranslationCommandExpected = null,
    [property: JsonPropertyName("boundary")] string? Boundary = null,
    [property: JsonPropertyName("boundaryTarget")] string? BoundaryTarget = null)
{
    [JsonIgnore]
    public string Set
    {
        get
        {
            var separator = Id.IndexOf('/');
            return separator > 0 ? Id[..separator] : "default";
        }
    }
}

public sealed record CorpusReferenceDocument(
    CorpusReferenceManifest? Manifest,
    IReadOnlyList<CorpusEntry> Entries);

/// <summary>
/// One in-memory scored clip. Reference/hypothesis and expectations are deliberately ignored by
/// JSON serialization: a benchmark report may be attached to a bug without publishing the user's
/// dictated text. Only aggregate counts and stable IDs leave the process.
/// </summary>
public sealed record BenchmarkEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("set")] string Set,
    [property: JsonIgnore] string Reference = "",
    [property: JsonIgnore] string Hypothesis = "",
    [property: JsonPropertyName("perceivedMs")] double PerceivedMs = 0,
    [property: JsonPropertyName("totalMs")] double TotalMs = 0,
    [property: JsonPropertyName("errorCode")] string? Error = null,
    [property: JsonIgnore] IReadOnlyList<string>? ExpectedEntities = null,
    [property: JsonIgnore] bool? TranslationCommandExpected = null,
    [property: JsonIgnore] string? Boundary = null,
    [property: JsonIgnore] string? BoundaryTarget = null,
    [property: JsonPropertyName("wordErrors")] int WordErrors = 0,
    [property: JsonPropertyName("referenceWords")] int ReferenceWords = 0,
    [property: JsonPropertyName("characterErrors")] int CharacterErrors = 0,
    [property: JsonPropertyName("referenceCharacters")] int ReferenceCharacters = 0,
    [property: JsonPropertyName("entitiesExpected")] int EntitiesExpected = 0,
    [property: JsonPropertyName("entitiesCorrect")] int EntitiesCorrect = 0,
    [property: JsonPropertyName("splitErrors")] int SplitErrors = 0,
    [property: JsonPropertyName("commandExpected")] bool? CommandExpected = null,
    [property: JsonPropertyName("commandDetected")] bool? CommandDetected = null,
    [property: JsonPropertyName("punctuationMatches")] int PunctuationMatches = 0,
    [property: JsonPropertyName("referencePunctuation")] int ReferencePunctuation = 0,
    [property: JsonPropertyName("hypothesisPunctuation")] int HypothesisPunctuation = 0,
    [property: JsonPropertyName("boundaryExpected")] bool BoundaryExpected = false,
    [property: JsonPropertyName("boundaryCorrect")] bool BoundaryCorrect = false,
    [property: JsonPropertyName("buckets")] IReadOnlyList<string>? Buckets = null,
    [property: JsonPropertyName("captureCode")] string CaptureCode = "CaptureNotMeasured",
    [property: JsonPropertyName("gateCode")] string GateCode = "GateNotMeasured",
    [property: JsonPropertyName("attributionCode")] string AttributionCode = "NotAttributed",
    [property: JsonPropertyName("fallbackTrigger")] string FallbackTrigger = "None",
    [property: JsonPropertyName("fallbackRan")] bool FallbackRan = false,
    [property: JsonPropertyName("fallbackUnavailable")] bool FallbackUnavailable = false,
    [property: JsonPropertyName("selectedEngine")] string? SelectedEngine = null,
    [property: JsonPropertyName("primaryWordErrors")] int? PrimaryWordErrors = null,
    [property: JsonPropertyName("fallbackWordErrors")] int? FallbackWordErrors = null,
    [property: JsonPropertyName("selectedWordErrors")] int? SelectedWordErrors = null,
    [property: JsonPropertyName("stageMs")] BenchmarkEntryStageTimings? StageTimings = null);

public sealed record BenchmarkEntryStageTimings(
    [property: JsonPropertyName("captureAnalysis")] double CaptureAnalysisMs,
    [property: JsonPropertyName("primaryDecode")] double? PrimaryDecodeMs,
    [property: JsonPropertyName("fallbackDecode")] double? FallbackDecodeMs,
    [property: JsonPropertyName("selection")] double SelectionMs,
    [property: JsonPropertyName("normalization")] double NormalizationMs,
    [property: JsonPropertyName("pipeline")] double PipelineMs);

public sealed record BenchmarkSetSummary(
    [property: JsonPropertyName("set")] string Set,
    [property: JsonPropertyName("clips")] int Clips,
    [property: JsonPropertyName("wer")] double WordErrorRate,
    [property: JsonPropertyName("cer")] double CharacterErrorRate,
    [property: JsonPropertyName("failedClips")] int FailedClips = 0,
    [property: JsonPropertyName("entityAccuracy")] double EntityAccuracy = 1,
    [property: JsonPropertyName("entitiesExpected")] int EntitiesExpected = 0,
    [property: JsonPropertyName("splitErrors")] int SplitErrors = 0,
    [property: JsonPropertyName("commandPrecision")] double CommandPrecision = 1,
    [property: JsonPropertyName("commandRecall")] double CommandRecall = 1,
    [property: JsonPropertyName("punctuationF1")] double PunctuationF1 = 1,
    [property: JsonPropertyName("boundaryAccuracy")] double BoundaryAccuracy = 1);

public sealed record BenchmarkModelFingerprint(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record BenchmarkEnvironment(
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("appSha256")] string? AppSha256,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("os")] string OperatingSystem,
    [property: JsonPropertyName("processArchitecture")] string ProcessArchitecture,
    [property: JsonPropertyName("cpu")] string? Cpu,
    [property: JsonPropertyName("logicalProcessors")] int LogicalProcessors,
    [property: JsonPropertyName("models")] IReadOnlyList<BenchmarkModelFingerprint> Models);

public sealed record BenchmarkParameters(
    [property: JsonPropertyName("pipeline")] string Pipeline,
    [property: JsonPropertyName("inputSampleRateHz")] int InputSampleRateHz,
    [property: JsonPropertyName("capturePreRollMs")] double CapturePreRollMs,
    [property: JsonPropertyName("captureReleaseTailMs")] double CaptureReleaseTailMs,
    [property: JsonPropertyName("gigaAmThreads")] int GigaAmThreads,
    [property: JsonPropertyName("gigaAmBatchThreshold")] int GigaAmBatchThreshold,
    [property: JsonPropertyName("gigaAmMaxBatchSize")] int GigaAmMaxBatchSize,
    [property: JsonPropertyName("gigaAmContextualBias")] bool GigaAmContextualBias,
    [property: JsonPropertyName("gigaAmHotwordVersion")] string? GigaAmHotwordVersion,
    [property: JsonPropertyName("gigaAmHotwordScore")] float? GigaAmHotwordScore,
    [property: JsonPropertyName("whisperThreads")] int WhisperThreads,
    [property: JsonPropertyName("whisperRuntimePreference")] string WhisperRuntimePreference,
    [property: JsonPropertyName("whisperRuntimeLoaded")] string WhisperRuntimeLoaded,
    [property: JsonPropertyName("whisperLanguageDetection")] bool WhisperLanguageDetection,
    [property: JsonPropertyName("whisperSampling")] string WhisperSampling,
    [property: JsonPropertyName("whisperNoContext")] bool WhisperNoContext,
    [property: JsonPropertyName("mixedLanguageMode")] bool MixedLanguageMode,
    [property: JsonPropertyName("entityCatalogVersion")] string EntityCatalogVersion,
    [property: JsonPropertyName("entityProfilePolicy")] string EntityProfilePolicy,
    [property: JsonPropertyName("applyBuiltInDictionary")] bool ApplyBuiltInDictionary,
    [property: JsonPropertyName("applyVoiceCommands")] bool ApplyVoiceCommands,
    [property: JsonPropertyName("applyNumberNormalization")] bool ApplyNumberNormalization,
    [property: JsonPropertyName("modelDownloadAllowed")] bool ModelDownloadAllowed);

public sealed record BenchmarkResourceSnapshot(
    long WorkingSetBytes,
    long PrivateBytes,
    long PeakWorkingSetBytes,
    int HandleCount,
    long ManagedBytes)
{
    public static BenchmarkResourceSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new BenchmarkResourceSnapshot(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.PeakWorkingSet64,
            OperatingSystem.IsWindows() ? process.HandleCount : 0,
            GC.GetTotalMemory(forceFullCollection: false));
    }
}

public sealed record BenchmarkResourceSummary(
    [property: JsonPropertyName("workingSetStartBytes")] long WorkingSetStartBytes,
    [property: JsonPropertyName("workingSetEndBytes")] long WorkingSetEndBytes,
    [property: JsonPropertyName("peakWorkingSetBytes")] long PeakWorkingSetBytes,
    [property: JsonPropertyName("privateStartBytes")] long PrivateStartBytes,
    [property: JsonPropertyName("privateEndBytes")] long PrivateEndBytes,
    [property: JsonPropertyName("handlesStart")] int HandlesStart,
    [property: JsonPropertyName("handlesEnd")] int HandlesEnd,
    [property: JsonPropertyName("managedStartBytes")] long ManagedStartBytes,
    [property: JsonPropertyName("managedEndBytes")] long ManagedEndBytes);

public sealed record CorpusInventory(
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("clips")] int Clips,
    [property: JsonPropertyName("audioBytes")] long AudioBytes,
    [property: JsonPropertyName("scriptSha256")] string ScriptSha256);

public sealed record BenchmarkProfileSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("clips")] int Clips,
    [property: JsonPropertyName("buckets")] IReadOnlyDictionary<string, int> Buckets);

public sealed record BenchmarkUiStallSummary(
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("p50Ms")] double P50Ms,
    [property: JsonPropertyName("p95Ms")] double P95Ms,
    [property: JsonPropertyName("maxMs")] double MaxMs,
    [property: JsonPropertyName("over100Ms")] int Over100Ms);

public sealed record BenchmarkStageTimingSummary(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("p50Ms")] double P50Ms,
    [property: JsonPropertyName("p95Ms")] double P95Ms);

public sealed record BenchmarkCodeCount(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("clips")] int Clips);

public sealed record BenchmarkBucketSummary(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("clips")] int Clips,
    [property: JsonPropertyName("failedClips")] int FailedClips,
    [property: JsonPropertyName("wer")] double WordErrorRate,
    [property: JsonPropertyName("gateAcceptedClips")] int GateAcceptedClips,
    [property: JsonPropertyName("fallbackRequestedClips")] int FallbackRequestedClips);

public sealed record BenchmarkDiagnosticsSummary(
    [property: JsonPropertyName("captureCodes")] IReadOnlyList<BenchmarkCodeCount> CaptureCodes,
    [property: JsonPropertyName("gateCodes")] IReadOnlyList<BenchmarkCodeCount> GateCodes,
    [property: JsonPropertyName("attributions")] IReadOnlyList<BenchmarkCodeCount> Attributions,
    [property: JsonPropertyName("stageTimings")] IReadOnlyList<BenchmarkStageTimingSummary> StageTimings,
    [property: JsonPropertyName("buckets")] IReadOnlyList<BenchmarkBucketSummary> Buckets,
    [property: JsonPropertyName("fallbackRequestedClips")] int FallbackRequestedClips,
    [property: JsonPropertyName("fallbackRanClips")] int FallbackRanClips,
    [property: JsonPropertyName("fallbackUnavailableClips")] int FallbackUnavailableClips,
    [property: JsonPropertyName("fallbackRequestRate")] double FallbackRequestRate,
    [property: JsonPropertyName("fallbackRunRate")] double FallbackRunRate,
    [property: JsonPropertyName("uiThreadStalls")] BenchmarkUiStallSummary? UiThreadStalls);

internal sealed record BenchmarkClipDiagnostics(
    string CaptureCode,
    string GateCode,
    string AttributionCode,
    string FallbackTrigger,
    bool FallbackRan,
    bool FallbackUnavailable,
    string SelectedEngine,
    int? PrimaryWordErrors,
    int? FallbackWordErrors,
    int SelectedWordErrors,
    BenchmarkEntryStageTimings StageTimings);

public sealed record BenchmarkRunContext(
    CorpusInventory Corpus,
    BenchmarkEnvironment Environment,
    BenchmarkParameters Parameters,
    BenchmarkResourceSnapshot StartResources,
    BenchmarkResourceSnapshot EndResources,
    BenchmarkProfileSummary? Profile = null,
    BenchmarkUiStallSummary? UiThreadStalls = null);

public sealed record BenchmarkReport(
    [property: JsonPropertyName("generatedUtc")] DateTime GeneratedUtc,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("wer")] double WordErrorRate,
    [property: JsonPropertyName("cer")] double CharacterErrorRate,
    [property: JsonPropertyName("p50Ms")] double MedianMs,
    [property: JsonPropertyName("p95Ms")] double P95Ms,
    [property: JsonPropertyName("sets")] IReadOnlyList<BenchmarkSetSummary> Sets,
    [property: JsonPropertyName("entries")] IReadOnlyList<BenchmarkEntry> Entries,
    [property: JsonPropertyName("schema")] string Schema = "egoist.voice.corpus-benchmark/v3",
    [property: JsonPropertyName("privacy")] string Privacy = "aggregate-only-no-transcript",
    [property: JsonPropertyName("failedClips")] int FailedClips = 0,
    [property: JsonPropertyName("entityAccuracy")] double EntityAccuracy = 1,
    [property: JsonPropertyName("entitiesExpected")] int EntitiesExpected = 0,
    [property: JsonPropertyName("splitErrors")] int SplitErrors = 0,
    [property: JsonPropertyName("commandPrecision")] double CommandPrecision = 1,
    [property: JsonPropertyName("commandRecall")] double CommandRecall = 1,
    [property: JsonPropertyName("punctuationF1")] double PunctuationF1 = 1,
    [property: JsonPropertyName("boundaryAccuracy")] double BoundaryAccuracy = 1,
    [property: JsonPropertyName("corpus")] CorpusInventory? Corpus = null,
    [property: JsonPropertyName("environment")] BenchmarkEnvironment? Environment = null,
    [property: JsonPropertyName("parameters")] BenchmarkParameters? Parameters = null,
    [property: JsonPropertyName("resources")] BenchmarkResourceSummary? Resources = null,
    [property: JsonPropertyName("profile")] BenchmarkProfileSummary? Profile = null,
    [property: JsonPropertyName("diagnostics")] BenchmarkDiagnosticsSummary? Diagnostics = null);

public sealed record BenchmarkFailureReport(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("errorCode")] string ErrorCode);

/// <summary>
/// Crash-resilient local cursor for long corpus runs. It contains only the same stable IDs and
/// aggregate counts already allowed in a benchmark report, never audio, text or a filesystem path.
/// </summary>
public sealed record BenchmarkProgressReport(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("completedClips")] int CompletedClips,
    [property: JsonPropertyName("totalClips")] int TotalClips,
    [property: JsonPropertyName("currentId")] string? CurrentId);

public static class CorpusBenchmark
{
    public const string ReferenceFileName = "reference.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex SafeLabel = new(
        "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string ValidateLabel(string label)
    {
        if (!SafeLabel.IsMatch(label))
        {
            throw new InvalidDataException(
                "Benchmark label должен содержать 1–80 символов: A-Z, a-z, 0-9, точку, '_' или '-'.");
        }
        return label;
    }

    public static IReadOnlyList<CorpusEntry> LoadReferences(string corpusDirectory) =>
        LoadReferenceDocument(corpusDirectory).Entries;

    public static CorpusReferenceDocument LoadReferenceDocument(string corpusDirectory)
    {
        var path = Path.Combine(corpusDirectory, ReferenceFileName);
        if (!File.Exists(path))
        {
            return new CorpusReferenceDocument(null, []);
        }

        CorpusReferenceManifest? manifest = null;
        var entries = new List<CorpusEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.TryGetProperty("kind", out var kind) &&
                    kind.ValueKind == JsonValueKind.String &&
                    string.Equals(kind.GetString(), "corpus-reference", StringComparison.Ordinal))
                {
                    if (manifest is not null || entries.Count > 0)
                    {
                        throw new InvalidDataException("Reference manifest должен быть первой JSON-строкой и встречаться один раз.");
                    }
                    manifest = JsonSerializer.Deserialize<CorpusReferenceManifest>(trimmed, Json)
                        ?? throw new InvalidDataException("Пустой reference manifest.");
                    continue;
                }

                var entry = JsonSerializer.Deserialize<CorpusEntry>(trimmed, Json)
                    ?? throw new InvalidDataException("Пустая corpus entry.");
                ValidateEntry(entry);
                if (!ids.Add(entry.Id))
                {
                    throw new InvalidDataException($"Дублирующийся reference id: {entry.Id}");
                }
                entries.Add(entry);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Некорректный JSON в {ReferenceFileName}, строка {lineNumber}.", exception);
            }
        }

        return new CorpusReferenceDocument(manifest, entries);
    }

    public static CorpusInventory ValidateAndFingerprint(
        string corpusDirectory,
        CorpusScript script,
        CorpusReferenceDocument references,
        IReadOnlyCollection<string>? selectedIds = null)
    {
        var manifest = references.Manifest
            ?? throw new InvalidDataException($"{ReferenceFileName} не содержит versioned manifest.");
        if (manifest.SchemaVersion != CorpusScript.CurrentSchemaVersion ||
            !string.Equals(manifest.Privacy, CorpusScript.PrivateDataPolicy, StringComparison.Ordinal) ||
            !string.Equals(manifest.ScriptSha256, script.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Reference manifest не совпадает со schema/privacy/hash текущего script.jsonl.");
        }

        var scriptIds = script.Lines.Select(line => line.Id).ToHashSet(StringComparer.Ordinal);
        var expectedIds = selectedIds is null
            ? scriptIds
            : selectedIds.ToHashSet(StringComparer.Ordinal);
        if (!expectedIds.IsSubsetOf(scriptIds))
        {
            throw new InvalidDataException("Corpus profile содержит id вне текущего script.jsonl.");
        }
        var actualIds = references.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var missing = expectedIds.Except(actualIds, StringComparer.Ordinal).Order().ToArray();
        var unexpected = actualIds.Except(scriptIds, StringComparer.Ordinal).Order().ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
        {
            throw new InvalidDataException(
                $"Корпус неполон или не соответствует скрипту: missing={missing.Length}, unexpected={unexpected.Length}.");
        }

        var root = Path.GetFullPath(corpusDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, manifest.ScriptSha256);
        long audioBytes = 0;
        var selectedEntries = references.Entries
            .Where(entry => expectedIds.Contains(entry.Id))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var entry in selectedEntries)
        {
            var audioPath = Path.GetFullPath(Path.Combine(root, entry.Audio));
            if (!audioPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Audio path выходит за пределы корпуса: {entry.Id}");
            }
            if (!File.Exists(audioPath) || new FileInfo(audioPath).Length <= 44)
            {
                throw new InvalidDataException($"Нет непустого WAV для corpus id: {entry.Id}");
            }

            AppendHash(hash, entry.Id);
            AppendHash(hash, entry.Text);
            AppendHash(hash, string.Join('\u001e', entry.Tags ?? []));
            AppendHash(hash, string.Join('\u001e', entry.Entities ?? []));
            AppendHash(hash, entry.TranslationCommandExpected?.ToString() ?? string.Empty);
            AppendHash(hash, entry.Boundary ?? string.Empty);
            AppendHash(hash, entry.BoundaryTarget ?? string.Empty);

            using var stream = File.OpenRead(audioPath);
            audioBytes += stream.Length;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return new CorpusInventory(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            selectedEntries.Length,
            audioBytes,
            manifest.ScriptSha256);
    }

    public static BenchmarkProfileSummary SummarizeProfile(CorpusBenchmarkProfile profile) => new(
        profile.Id,
        profile.Fingerprint,
        profile.SelectedIds.Count,
        profile.Buckets.ToDictionary(
            bucket => bucket.Key,
            bucket => bucket.Value.Count,
            StringComparer.Ordinal));

    public static BenchmarkEnvironment CaptureEnvironment(IReadOnlyList<ModelDescriptor> models)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var entry = entryAssembly?.GetName();
        // Environment.ProcessPath is the stable .NET apphost and can remain byte-identical while
        // Egoist.Voice.dll changes. Bind benchmark evidence to the managed entry assembly instead.
        var applicationPath = string.IsNullOrWhiteSpace(entryAssembly?.Location)
            ? Environment.ProcessPath
            : entryAssembly.Location;
        return new BenchmarkEnvironment(
            entry?.Version?.ToString() ?? "unknown",
            TryHashFile(applicationPath),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            TryReadCpuName(),
            Environment.ProcessorCount,
            models.OrderBy(model => model.Id, StringComparer.Ordinal)
                .Select(model => new BenchmarkModelFingerprint(model.Id, model.SizeBytes, model.Sha256))
                .ToArray());
    }

    public static BenchmarkParameters CaptureParameters(
        bool enableContextualBias = false,
        string whisperRuntimePreference = "auto",
        string whisperRuntimeLoaded = "not-loaded")
    {
        enableContextualBias &= RussianAsrProfile.ContextualBiasSupported;
        var postProcessing = PostProcessingOptions.Default;
        return new BenchmarkParameters(
            nameof(HybridTranscriptionService),
            GigaAmTranscriptionService.BenchmarkSampleRate,
            AudioCaptureService.PreRollDuration.TotalMilliseconds,
            AudioCaptureService.ReleaseTailMinimum.TotalMilliseconds,
            GigaAmTranscriptionService.BenchmarkDecodeThreads,
            GigaAmTranscriptionService.BenchmarkBatchDecodeThreshold,
            GigaAmTranscriptionService.BenchmarkMaxBatchSize,
            GigaAmContextualBias: enableContextualBias,
            GigaAmHotwordVersion: enableContextualBias ? GigaAmHotwordResources.Version : null,
            GigaAmHotwordScore: enableContextualBias ? GigaAmHotwordResources.GlobalScore : null,
            WhisperTranscriptionService.BenchmarkDecodeThreads,
            WhisperRuntimePreference: whisperRuntimePreference,
            WhisperRuntimeLoaded: whisperRuntimeLoaded,
            WhisperLanguageDetection: true,
            WhisperSampling: "beam-search/default-size",
            WhisperNoContext: true,
            MixedLanguageMode: false,
            EntityCatalogVersion: BuiltInVocabulary.Version,
            EntityProfilePolicy: "target-and-utterance/v1",
            ApplyBuiltInDictionary: postProcessing.ApplyDictionary,
            ApplyVoiceCommands: postProcessing.ApplyVoiceCommands,
            ApplyNumberNormalization: postProcessing.ApplyNumberNormalization,
            ModelDownloadAllowed: false);
    }

    public static BenchmarkReport Summarize(
        string label,
        IReadOnlyList<BenchmarkEntry> entries,
        ScoringOptions? options = null,
        BenchmarkRunContext? context = null)
    {
        ValidateLabel(label);
        options ??= ScoringOptions.Default;
        var analyzed = entries.Select(entry => Analyze(entry, options)).ToArray();
        var successful = analyzed.Where(entry => entry.Error is null).ToArray();
        var overall = RecognitionScorer.Aggregate(successful.Select(ToRecognitionScore));

        var sets = analyzed
            .GroupBy(entry => entry.Set, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(SummarizeSet)
            .ToArray();

        var latencies = successful.Select(entry => TimeSpan.FromMilliseconds(entry.PerceivedMs)).ToArray();
        var entityExpected = successful.Sum(entry => entry.EntitiesExpected);
        var entityCorrect = successful.Sum(entry => entry.EntitiesCorrect);
        var commands = CommandCounts(successful);
        var punctuation = PunctuationCounts(successful);
        var boundaryExpected = successful.Count(entry => entry.BoundaryExpected);
        var boundaryCorrect = successful.Count(entry => entry.BoundaryExpected && entry.BoundaryCorrect);
        var resources = context is null ? null : SummarizeResources(context.StartResources, context.EndResources);
        var diagnostics = SummarizeDiagnostics(analyzed, context?.UiThreadStalls);

        return new BenchmarkReport(
            DateTime.UtcNow,
            label,
            overall.WordErrorRate,
            overall.CharacterErrorRate,
            LatencyStatistics.Median(latencies).TotalMilliseconds,
            LatencyStatistics.Percentile(latencies, 0.95).TotalMilliseconds,
            sets,
            analyzed,
            FailedClips: analyzed.Count(entry => entry.Error is not null),
            EntityAccuracy: Ratio(entityCorrect, entityExpected),
            EntitiesExpected: entityExpected,
            SplitErrors: successful.Sum(entry => entry.SplitErrors),
            CommandPrecision: Ratio(commands.TruePositive, commands.TruePositive + commands.FalsePositive),
            CommandRecall: Ratio(commands.TruePositive, commands.Expected),
            PunctuationF1: F1(punctuation.Matches, punctuation.Hypothesis, punctuation.Reference),
            BoundaryAccuracy: Ratio(boundaryCorrect, boundaryExpected),
            Corpus: context?.Corpus,
            Environment: context?.Environment,
            Parameters: context?.Parameters,
            Resources: resources,
            Profile: context?.Profile,
            Diagnostics: diagnostics);
    }

    internal static BenchmarkClipDiagnostics AnalyzeStages(
        string reference,
        float[] samples,
        SpeechActivitySnapshot activity,
        HybridTranscriptionObservation observation,
        string normalizedText,
        TimeSpan captureAnalysisElapsed,
        TimeSpan normalizationElapsed)
    {
        int? primaryErrors = observation.Primary is null
            ? null
            : RecognitionScorer.Score(reference, observation.Primary.Text).WordErrors;
        int? fallbackErrors = observation.Fallback is null
            ? null
            : RecognitionScorer.Score(reference, observation.Fallback.Text).WordErrors;
        var selectedErrors = RecognitionScorer.Score(reference, observation.Result.Text).WordErrors;
        var finalErrors = RecognitionScorer.Score(reference, normalizedText).WordErrors;
        var captureCode = ClassifyCapture(samples, activity);
        var gateCode = ClassifyGate(activity);
        var attribution = AttributionCode(
            captureCode,
            gateCode,
            finalErrors,
            primaryErrors,
            fallbackErrors,
            selectedErrors,
            observation);

        return new BenchmarkClipDiagnostics(
            captureCode,
            gateCode,
            attribution,
            observation.FallbackTrigger.ToString(),
            observation.FallbackRan,
            observation.FallbackUnavailable,
            observation.SelectedEngine,
            primaryErrors,
            fallbackErrors,
            selectedErrors,
            new BenchmarkEntryStageTimings(
                captureAnalysisElapsed.TotalMilliseconds,
                observation.Primary?.Elapsed.TotalMilliseconds,
                observation.Fallback?.Elapsed.TotalMilliseconds,
                observation.SelectionElapsed.TotalMilliseconds,
                normalizationElapsed.TotalMilliseconds,
                observation.PipelineElapsed.TotalMilliseconds + normalizationElapsed.TotalMilliseconds));
    }

    public static void Save(BenchmarkReport report, string path)
    {
        WriteAtomic(path, report);
    }

    public static void SaveFailure(string path, string label, string errorCode)
    {
        var safeLabel = SafeLabel.IsMatch(label) ? label : "invalid-label";
        var report = new BenchmarkFailureReport(
            "egoist.voice.corpus-benchmark/v3",
            "failed",
            safeLabel,
            errorCode);
        WriteAtomic(path, report);
    }

    public static void SaveProgress(
        string path,
        string label,
        string phase,
        int completedClips,
        int totalClips,
        string? currentId)
    {
        ValidateLabel(label);
        if (phase is not ("started" or "completed" or "complete") ||
            completedClips < 0 || totalClips <= 0 || completedClips > totalClips)
        {
            throw new InvalidDataException("Некорректный benchmark progress cursor.");
        }
        if (currentId is not null)
        {
            CorpusScript.ValidateId(currentId);
        }
        if ((phase == "complete") != (currentId is null) ||
            (phase == "complete" && completedClips != totalClips))
        {
            throw new InvalidDataException("Benchmark progress phase не соответствует cursor state.");
        }

        WriteAtomic(
            path,
            new BenchmarkProgressReport(
                "egoist.voice.corpus-benchmark-progress/v1",
                label,
                phase,
                completedClips,
                totalClips,
                currentId));
    }

    public static BenchmarkReport? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(path), Json) : null;

    public static IReadOnlyList<string> CompareToBaseline(
        BenchmarkReport baseline,
        BenchmarkReport candidate,
        double maxWordErrorRateIncrease = 0.005,
        double maxLatencyIncreaseRatio = 0.15)
    {
        var breaches = new List<string>();
        if (baseline.Corpus is null || candidate.Corpus is null)
        {
            breaches.Add("Corpus SHA-256 отсутствует: сравнение незамороженных отчётов запрещено.");
        }
        else if (!string.Equals(baseline.Corpus.Sha256, candidate.Corpus.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            breaches.Add("Corpus SHA-256 не совпадает: сравнение разных записей запрещено.");
        }

        if ((baseline.Profile is null) != (candidate.Profile is null))
        {
            breaches.Add("Corpus profile не совпадает: нельзя сравнивать полный и focused прогоны.");
        }
        else if (baseline.Profile is not null && candidate.Profile is not null &&
            (!string.Equals(baseline.Profile.Id, candidate.Profile.Id, StringComparison.Ordinal) ||
             !string.Equals(baseline.Profile.Sha256, candidate.Profile.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            breaches.Add("Corpus profile SHA-256 не совпадает: bucket/selection manifest изменён.");
        }

        var werDelta = candidate.WordErrorRate - baseline.WordErrorRate;
        if (werDelta > maxWordErrorRateIncrease)
        {
            breaches.Add(
                $"WER вырос на {werDelta * 100:0.00} п.п. " +
                $"({baseline.WordErrorRate * 100:0.00}% → {candidate.WordErrorRate * 100:0.00}%), " +
                $"допуск {maxWordErrorRateIncrease * 100:0.00} п.п.");
        }

        foreach (var baselineSet in baseline.Sets)
        {
            var candidateSet = candidate.Sets.FirstOrDefault(set => set.Set == baselineSet.Set);
            if (candidateSet is null)
            {
                breaches.Add($"Набор «{baselineSet.Set}» отсутствует в новом прогоне.");
                continue;
            }

            var setDelta = candidateSet.WordErrorRate - baselineSet.WordErrorRate;
            if (setDelta > maxWordErrorRateIncrease)
            {
                breaches.Add(
                    $"Набор «{baselineSet.Set}»: WER вырос на {setDelta * 100:0.00} п.п. " +
                    $"({baselineSet.WordErrorRate * 100:0.00}% → {candidateSet.WordErrorRate * 100:0.00}%).");
            }
        }

        if (baseline.P95Ms > 0)
        {
            var latencyRatio = (candidate.P95Ms - baseline.P95Ms) / baseline.P95Ms;
            if (latencyRatio > maxLatencyIncreaseRatio)
            {
                breaches.Add(
                    $"p95 latency вырос на {latencyRatio * 100:0.0}% " +
                    $"({baseline.P95Ms:0} мс → {candidate.P95Ms:0} мс), допуск {maxLatencyIncreaseRatio * 100:0}%.");
            }
        }

        return breaches;
    }

    private static BenchmarkEntry Analyze(BenchmarkEntry entry, ScoringOptions options)
    {
        if (entry.Error is not null)
        {
            return entry with { Reference = string.Empty, Hypothesis = string.Empty };
        }

        var score = RecognitionScorer.Score(entry.Reference, entry.Hypothesis, options);
        var entities = entry.ExpectedEntities ?? [];
        var correctEntities = entities.Count(entity => entry.Hypothesis.Contains(entity, StringComparison.Ordinal));
        var splitErrors = entities.Count(entity =>
            !entry.Hypothesis.Contains(entity, StringComparison.Ordinal) &&
            Compact(entry.Hypothesis).Contains(Compact(entity), StringComparison.OrdinalIgnoreCase));
        var commandDetected = TranslateCommandParser.TryParse(entry.Hypothesis) is not null;
        var punctuation = ComparePunctuation(entry.Reference, entry.Hypothesis);
        var boundaryExpected = entry.Boundary is not null;
        var boundaryCorrect = boundaryExpected && BoundaryMatches(
            entry.Hypothesis,
            entry.Boundary!,
            entry.BoundaryTarget ?? string.Empty);

        return entry with
        {
            WordErrors = score.WordErrors,
            ReferenceWords = score.ReferenceWords,
            CharacterErrors = score.CharacterErrors,
            ReferenceCharacters = score.ReferenceCharacters,
            EntitiesExpected = entities.Count,
            EntitiesCorrect = correctEntities,
            SplitErrors = splitErrors,
            CommandExpected = entry.TranslationCommandExpected,
            CommandDetected = entry.TranslationCommandExpected is null ? null : commandDetected,
            PunctuationMatches = punctuation.Matches,
            ReferencePunctuation = punctuation.Reference,
            HypothesisPunctuation = punctuation.Hypothesis,
            BoundaryExpected = boundaryExpected,
            BoundaryCorrect = boundaryCorrect,
            Reference = string.Empty,
            Hypothesis = string.Empty,
            ExpectedEntities = null,
            TranslationCommandExpected = null,
            Boundary = null,
            BoundaryTarget = null
        };
    }

    private static BenchmarkDiagnosticsSummary SummarizeDiagnostics(
        IReadOnlyList<BenchmarkEntry> entries,
        BenchmarkUiStallSummary? uiThreadStalls)
    {
        var successful = entries.Where(entry => entry.Error is null).ToArray();
        var stageTimings = new[]
        {
            SummarizeStage("capture-analysis", successful, entry => entry.StageTimings?.CaptureAnalysisMs),
            SummarizeStage("primary-decode", successful, entry => entry.StageTimings?.PrimaryDecodeMs),
            SummarizeStage("fallback-decode", successful, entry => entry.StageTimings?.FallbackDecodeMs),
            SummarizeStage("selection", successful, entry => entry.StageTimings?.SelectionMs),
            SummarizeStage("normalization", successful, entry => entry.StageTimings?.NormalizationMs),
            SummarizeStage("pipeline", successful, entry => entry.StageTimings?.PipelineMs)
        }.Where(summary => summary.Samples > 0).ToArray();

        var buckets = entries
            .SelectMany(entry => (entry.Buckets ?? []).Select(bucket => (Bucket: bucket, Entry: entry)))
            .GroupBy(item => item.Bucket, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var bucketEntries = group.Select(item => item.Entry).ToArray();
                var scored = bucketEntries.Where(entry => entry.Error is null).ToArray();
                var score = RecognitionScorer.Aggregate(scored.Select(ToRecognitionScore));
                return new BenchmarkBucketSummary(
                    group.Key,
                    bucketEntries.Length,
                    bucketEntries.Count(entry => entry.Error is not null),
                    score.WordErrorRate,
                    scored.Count(entry => entry.GateCode == "GateAccepted"),
                    scored.Count(entry => entry.FallbackTrigger != MixedSpeechTrigger.None.ToString()));
            })
            .ToArray();

        var fallbackRequested = successful.Count(entry =>
            entry.FallbackTrigger != MixedSpeechTrigger.None.ToString());
        var fallbackRan = successful.Count(entry => entry.FallbackRan);
        return new BenchmarkDiagnosticsSummary(
            CountCodes(entries.Select(entry => entry.CaptureCode)),
            CountCodes(entries.Select(entry => entry.GateCode)),
            CountCodes(entries.Select(entry => entry.AttributionCode)),
            stageTimings,
            buckets,
            fallbackRequested,
            fallbackRan,
            successful.Count(entry => entry.FallbackUnavailable),
            successful.Length == 0 ? 0 : fallbackRequested / (double)successful.Length,
            successful.Length == 0 ? 0 : fallbackRan / (double)successful.Length,
            uiThreadStalls);
    }

    private static IReadOnlyList<BenchmarkCodeCount> CountCodes(IEnumerable<string> codes) => codes
        .GroupBy(code => code, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new BenchmarkCodeCount(group.Key, group.Count()))
        .ToArray();

    private static BenchmarkStageTimingSummary SummarizeStage(
        string stage,
        IReadOnlyList<BenchmarkEntry> entries,
        Func<BenchmarkEntry, double?> selector)
    {
        var samples = entries
            .Select(selector)
            .Where(value => value is >= 0)
            .Select(value => TimeSpan.FromMilliseconds(value!.Value))
            .ToArray();
        return new BenchmarkStageTimingSummary(
            stage,
            samples.Length,
            LatencyStatistics.Median(samples).TotalMilliseconds,
            LatencyStatistics.Percentile(samples, 0.95).TotalMilliseconds);
    }

    internal static string ClassifyCapture(float[] samples, SpeechActivitySnapshot activity)
    {
        if (samples.Length == 0 || activity.Duration <= TimeSpan.Zero)
        {
            return "CaptureNoAudio";
        }

        var clipped = samples.Count(sample => Math.Abs(sample) >= 0.999f);
        if (clipped >= Math.Max(3, samples.Length / 1000))
        {
            return "CaptureClipped";
        }
        return activity.PeakDecibels < -50 ? "CaptureVeryQuiet" : "CaptureUsable";
    }

    internal static string ClassifyGate(SpeechActivitySnapshot activity) =>
        activity.HasSpeech ? "GateAccepted" : $"Gate{activity.Rejection}";

    private static string AttributionCode(
        string captureCode,
        string gateCode,
        int finalErrors,
        int? primaryErrors,
        int? fallbackErrors,
        int selectedErrors,
        HybridTranscriptionObservation observation)
    {
        if (captureCode == "CaptureNoAudio")
        {
            return "CaptureIntegrity";
        }
        if (gateCode != "GateAccepted")
        {
            return "SpeechGate";
        }
        if (finalErrors == 0)
        {
            return "NoFailure";
        }
        if (captureCode == "CaptureClipped")
        {
            return "CaptureIntegrity";
        }
        if (observation.PrimaryFailed || primaryErrors is null)
        {
            return "PrimaryDecodeFailed";
        }
        if ((primaryErrors == 0 || fallbackErrors == 0) && selectedErrors > 0)
        {
            return "CandidateSelection";
        }
        if (finalErrors > selectedErrors)
        {
            return "Normalization";
        }
        if (observation.FallbackUnavailable && observation.FallbackTrigger != MixedSpeechTrigger.None)
        {
            return "FallbackUnavailable";
        }
        if (observation.FallbackFailed && primaryErrors > 0)
        {
            return "FallbackDecodeFailed";
        }
        if (primaryErrors > 0 && (fallbackErrors is null || fallbackErrors > 0))
        {
            return "DecoderOutput";
        }
        return "Unresolved";
    }

    private static BenchmarkSetSummary SummarizeSet(IGrouping<string, BenchmarkEntry> group)
    {
        var successful = group.Where(entry => entry.Error is null).ToArray();
        var score = RecognitionScorer.Aggregate(successful.Select(ToRecognitionScore));
        var entityExpected = successful.Sum(entry => entry.EntitiesExpected);
        var entityCorrect = successful.Sum(entry => entry.EntitiesCorrect);
        var commands = CommandCounts(successful);
        var punctuation = PunctuationCounts(successful);
        var boundaryExpected = successful.Count(entry => entry.BoundaryExpected);
        var boundaryCorrect = successful.Count(entry => entry.BoundaryExpected && entry.BoundaryCorrect);
        return new BenchmarkSetSummary(
            group.Key,
            successful.Length,
            score.WordErrorRate,
            score.CharacterErrorRate,
            group.Count(entry => entry.Error is not null),
            Ratio(entityCorrect, entityExpected),
            entityExpected,
            successful.Sum(entry => entry.SplitErrors),
            Ratio(commands.TruePositive, commands.TruePositive + commands.FalsePositive),
            Ratio(commands.TruePositive, commands.Expected),
            F1(punctuation.Matches, punctuation.Hypothesis, punctuation.Reference),
            Ratio(boundaryCorrect, boundaryExpected));
    }

    private static RecognitionScore ToRecognitionScore(BenchmarkEntry entry) => new(
        entry.ReferenceWords,
        entry.WordErrors,
        entry.ReferenceCharacters,
        entry.CharacterErrors);

    private static (int Expected, int TruePositive, int FalsePositive) CommandCounts(
        IEnumerable<BenchmarkEntry> entries)
    {
        var annotated = entries.Where(entry => entry.CommandExpected is not null).ToArray();
        return (
            annotated.Count(entry => entry.CommandExpected is true),
            annotated.Count(entry => entry.CommandExpected is true && entry.CommandDetected is true),
            annotated.Count(entry => entry.CommandExpected is false && entry.CommandDetected is true));
    }

    private static (int Matches, int Reference, int Hypothesis) PunctuationCounts(
        IEnumerable<BenchmarkEntry> entries) => entries.Aggregate(
            (Matches: 0, Reference: 0, Hypothesis: 0),
            (total, entry) => (
                total.Matches + entry.PunctuationMatches,
                total.Reference + entry.ReferencePunctuation,
                total.Hypothesis + entry.HypothesisPunctuation));

    private static (int Matches, int Reference, int Hypothesis) ComparePunctuation(
        string reference,
        string hypothesis)
    {
        var expected = ExtractPunctuation(reference);
        var actual = ExtractPunctuation(hypothesis);
        var previous = new int[actual.Length + 1];
        var current = new int[actual.Length + 1];
        for (var row = 1; row <= expected.Length; row++)
        {
            for (var column = 1; column <= actual.Length; column++)
            {
                current[column] = expected[row - 1] == actual[column - 1]
                    ? previous[column - 1] + 1
                    : Math.Max(previous[column], current[column - 1]);
            }
            (previous, current) = (current, previous);
            Array.Clear(current);
        }
        return (previous[actual.Length], expected.Length, actual.Length);
    }

    private static char[] ExtractPunctuation(string text) => text
        .Where(character => character is '.' or ',' or '!' or '?' or ':' or ';' or '—' or '–' or '\n')
        .ToArray();

    private static bool BoundaryMatches(string hypothesis, string boundary, string target)
    {
        var normalizedHypothesis = RecognitionScorer.Tokenize(hypothesis, ScoringOptions.Default);
        var normalizedTarget = RecognitionScorer.Tokenize(target, ScoringOptions.Default);
        if (normalizedTarget.Length == 0 || normalizedHypothesis.Length < normalizedTarget.Length)
        {
            return false;
        }
        return boundary == "start"
            ? normalizedHypothesis.Take(normalizedTarget.Length).SequenceEqual(normalizedTarget, StringComparer.Ordinal)
            : normalizedHypothesis.TakeLast(normalizedTarget.Length).SequenceEqual(normalizedTarget, StringComparer.Ordinal);
    }

    private static string Compact(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 1 : numerator / (double)denominator;

    private static double F1(int matches, int predicted, int expected)
    {
        if (predicted == 0 && expected == 0)
        {
            return 1;
        }
        return predicted + expected == 0 ? 0 : 2d * matches / (predicted + expected);
    }

    private static BenchmarkResourceSummary SummarizeResources(
        BenchmarkResourceSnapshot start,
        BenchmarkResourceSnapshot end) => new(
        start.WorkingSetBytes,
        end.WorkingSetBytes,
        Math.Max(start.PeakWorkingSetBytes, end.PeakWorkingSetBytes),
        start.PrivateBytes,
        end.PrivateBytes,
        start.HandleCount,
        end.HandleCount,
        start.ManagedBytes,
        end.ManagedBytes);

    private static void ValidateEntry(CorpusEntry entry)
    {
        CorpusScript.ValidateId(entry.Id);
        if (!string.Equals(entry.Audio, entry.Id + ".wav", StringComparison.Ordinal) ||
            Path.IsPathRooted(entry.Audio) || entry.Audio.Contains('\\') || entry.Audio.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Небезопасный audio path у corpus id: {entry.Id}");
        }
        if (string.IsNullOrWhiteSpace(entry.Text) || entry.Tags is null || entry.Tags.Count == 0)
        {
            throw new InvalidDataException($"Corpus id {entry.Id} не имеет text/tags privacy labels.");
        }
        if (entry.Boundary is not null && (entry.Boundary is not ("start" or "end") ||
            string.IsNullOrWhiteSpace(entry.BoundaryTarget)))
        {
            throw new InvalidDataException($"Corpus id {entry.Id} имеет некорректную boundary annotation.");
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string? TryHashFile(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadCpuName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null) as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
