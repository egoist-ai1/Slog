using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice;

/// <summary>
/// Лёгкий стенд сравнения конфигураций GigaAM: <c>--asr-eval &lt;каталог корпуса&gt; &lt;out.json&gt;</c>.
/// Опции читаются из окружения (EGOIST_EVAL_*); без них поведение равно продакшену портативного профиля.
/// Результат: { id: { h, ms, plain } }. Тексты в AppLog не попадают.
/// </summary>
public partial class App
{
    private static readonly JsonSerializerOptions AsrEvalJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private async Task RunAsrEvalAsync(string corpusDirectory, string outputPath)
    {
        try
        {
            using var sensitiveLogScope = AppLog.SuppressSensitiveData();
            corpusDirectory = Path.GetFullPath(corpusDirectory);

            static string? Env(string name) =>
                Environment.GetEnvironmentVariable("EGOIST_EVAL_" + name) is { Length: > 0 } value ? value.Trim() : null;

            var gainDb = Env("GAIN_DB") is { } gain ? double.Parse(gain, CultureInfo.InvariantCulture) : 0d;
            var tailMs = Env("TAIL_MS") is { } tail ? int.Parse(tail, CultureInfo.InvariantCulture) : 0;
            var threads = Env("THREADS") is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 4;
            var plainOnly = Env("PLAIN_ONLY") is "1" or "true";
            var encoder = (Env("ENCODER") ?? "int8").ToLowerInvariant();
            var decoding = (Env("DECODING") ?? "greedy").ToLowerInvariant();
            if (encoder is not ("int8" or "fp32")) throw new ArgumentException("EGOIST_EVAL_ENCODER: int8|fp32");
            if (decoding is not ("greedy" or "beam4")) throw new ArgumentException("EGOIST_EVAL_DECODING: greedy|beam4");

            var modelsRoot = Environment.GetEnvironmentVariable("EGOIST_VOICE_MODELS_ROOT") is { Length: > 0 } fromEnv
                ? Path.GetFullPath(fromEnv)
                : FindUpwards(@"artifacts\Russian-2.4.2-resource-verified\portable\Models")
                    ?? throw new DirectoryNotFoundException("Models root not found; set EGOIST_VOICE_MODELS_ROOT.");
            Environment.SetEnvironmentVariable("EGOIST_VOICE_MODELS_ROOT", modelsRoot);

            string? encoderOverride = null;
            if (encoder == "fp32")
            {
                encoderOverride = FindUpwards(@"artifacts\quality-2.3.1\float-exports\models\gigaam_v3_rnnt_encoder.onnx")
                    ?? throw new FileNotFoundException("fp32 encoder not found.");
            }

            var references = LoadAsrEvalReferences(Path.Combine(corpusDirectory, "reference.jsonl"));

            using var manager = new ModelManager(
                ModelCatalog.CreateRussianQualityModels(), modelsRoot, allowDownload: false);
            using var primary = new GigaAmTranscriptionService(manager, inferenceThreads: threads)
            {
                EncoderPathOverride = encoderOverride,
                DecodingMethodOverride = decoding == "beam4" ? "modified_beam_search" : null
            };
            using var service = new RussianSpeechQualityService(
                primary, GigaAmTranscriptionService.CreateFormattingEngine(manager, 4));
            service.FormatSpeechPunctuation = !plainOnly;
            await service.WarmUpAsync(null, CancellationToken.None);
            var postProcessor = new TranscriptPostProcessor(UserDictionary.BuiltIn);

            var gainFactor = Math.Pow(10, gainDb / 20d);
            var results = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var (id, audio) in references)
            {
                var samples = await Task.Run(() => AudioSampleReader.ReadMono16Khz(Path.Combine(corpusDirectory, audio)));
                if (gainDb != 0)
                {
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = Math.Clamp((float)(samples[i] * gainFactor), -1f, 1f);
                }
                if (tailMs > 0)
                {
                    var padded = new float[samples.Length + tailMs * 16];
                    samples.CopyTo(padded, 0);
                    samples = padded;
                }

                var started = Stopwatch.GetTimestamp();
                var result = await service.TranscribeSamplesAsync(samples, 16_000, CancellationToken.None);
                var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                string plain;
                if (plainOnly)
                {
                    plain = result.Text;
                }
                else
                {
                    // Чистый RNNT без оформления — отдельным вызовом, в ms не входит.
                    plain = (await ((ISampleTranscriptionService)primary)
                        .TranscribeSamplesAsync(samples, 16_000, CancellationToken.None)).Text;
                }

                var entityProfile = EntityProfilePolicy.Resolve(
                    result.Text, processName: null, isGame: false, technologyRequested: false);
                var text = postProcessor.Process(result.Text, entityProfile);
                results[id] = new Dictionary<string, object>
                {
                    ["h"] = text,
                    ["ms"] = Math.Round(elapsedMs, 1),
                    ["plain"] = plain
                };
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(results, AsrEvalJson), new UTF8Encoding(false));
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            // Текст исключения не содержит речи; для стенда нужна точная причина.
            Console.Error.WriteLine($"asr-eval failed: {exception.GetType().Name}: {exception.Message}");
            try
            {
                File.WriteAllText(outputPath + ".error.txt", exception.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Код возврата остаётся источником истины.
            }
        }
        finally
        {
            RequestShutdown();
        }
    }

    private static List<(string Id, string Audio)> LoadAsrEvalReferences(string path)
    {
        var list = new List<(string, string)>();
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.TrimStart('﻿').Trim();
            if (!line.StartsWith('{') || !line.Contains("\"id\"", StringComparison.Ordinal)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("audio", out var audio)) continue;
            list.Add((id.GetString()!, audio.GetString()!));
        }
        return list;
    }

    private static string? FindUpwards(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
