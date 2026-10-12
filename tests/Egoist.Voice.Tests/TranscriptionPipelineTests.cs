using System.Runtime.InteropServices;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

/// <summary>
/// Covers the phase-0 corrections to segmentation, chunk joining and candidate selection.
/// Each test states the defect it pins down, because every one of them passed silently before.
/// </summary>
public sealed class TranscriptionPipelineTests
{
    private const int SampleRate = 100;
    private const int MaxChunkSeconds = 22;
    private const int MaxChunkSamples = MaxChunkSeconds * SampleRate;

    [Theory]
    [InlineData(0.002f)]
    [InlineData(0.004f)]
    [InlineData(0.01f)]
    public void Continuous_quiet_signal_is_not_a_pause(float amplitude)
    {
        var samples = Constant(5_000, amplitude);

        var chunks = GigaAmAudioChunker.Split(samples, SampleRate, MaxChunkSeconds);

        Assert.Equal(MaxChunkSamples, chunks[0].Samples.Length);
        Assert.All(chunks, chunk => Assert.False(chunk.ParagraphBreakBefore));
        AssertContinuousCoverage(samples, chunks);
    }

    [Theory]
    [InlineData(0.004f)]
    [InlineData(0.04f)]
    public void Quiet_pause_boundary_is_invariant_to_recording_gain(float amplitude)
    {
        var samples = Constant(5_000, amplitude);
        for (var index = 1_850; index < 2_000; index++)
            samples[index] *= 0.1f;

        var chunks = GigaAmAudioChunker.Split(samples, SampleRate, MaxChunkSeconds);

        Assert.Equal(1_925, chunks[0].Samples.Length);
        Assert.True(chunks[1].ParagraphBreakBefore);
        AssertContinuousCoverage(samples, chunks);
    }

    private static void AssertContinuousCoverage(float[] original, IReadOnlyList<GigaAmAudioChunk> chunks)
    {
        var coveredEnd = 0;
        foreach (var chunk in chunks)
        {
            var segment = SegmentOf(chunk);
            Assert.Same(original, segment.Array);
            Assert.InRange(segment.Offset, 0, coveredEnd);
            Assert.True(segment.Offset + segment.Count > coveredEnd);
            coveredEnd = segment.Offset + segment.Count;
        }
        Assert.Equal(original.Length, coveredEnd);
    }

    [Fact]
    public void Chunker_defaults_to_35_second_continuous_window()
    {
        var samples = new float[30 * SampleRate];
        var chunks = GigaAmAudioChunker.Split(samples, SampleRate);
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunker_overlaps_every_boundary_including_detected_pauses()
    {
        // Previously overlap applied only to the hard-cut fallback: when a pause was found the
        // next chunk started exactly where the previous ended, and the engine lost the word
        // straddling the boundary.
        var samples = LoudSignal(5_000);
        Silence(samples, 1_900, 100);

        var chunks = GigaAmAudioChunker.Split(samples, SampleRate, MaxChunkSeconds);

        Assert.True(chunks.Count >= 2);
        var first = SegmentOf(chunks[0]);
        var second = SegmentOf(chunks[1]);
        Assert.True(
            second.Offset < first.Offset + first.Count,
            $"Второй фрагмент должен перекрывать первый: начало {second.Offset}, конец первого {first.Offset + first.Count}.");
    }

    [Fact]
    public void Chunker_prefers_the_longest_pause_over_the_rightmost_one()
    {
        // The old comparison kept whichever candidate sat closest to the 22-second limit, so a
        // short breath won over a real sentence break earlier in the window.
        var samples = LoudSignal(5_000);
        Silence(samples, 1_850, 150);
        Silence(samples, 2_100, 40);

        var chunks = GigaAmAudioChunker.Split(samples, SampleRate, MaxChunkSeconds);
        var first = SegmentOf(chunks[0]);

        Assert.InRange(first.Count, 1_850, 2_000);
    }

    [Fact]
    public void Digital_silence_is_still_a_pause()
    {
        var samples = new float[1_000];

        Assert.Equal(
            0,
            GigaAmAudioChunker.EstimateSilenceThreshold(samples, 2),
            precision: 6);
    }

    [Fact]
    public void Silence_threshold_rises_with_the_noise_floor_but_never_swallows_speech()
    {
        var quiet = Constant(1_000, 0.1f);
        Array.Fill(quiet, 0.01f, 0, 200);
        var noisy = Constant(1_000, 0.2f);

        var adapted = GigaAmAudioChunker.EstimateSilenceThreshold(quiet, 2);
        var capped = GigaAmAudioChunker.EstimateSilenceThreshold(noisy, 2);

        Assert.True(adapted > GigaAmAudioChunker.DefaultSilenceRms, "Порог должен подниматься при измеримом контрасте с шумом.");
        Assert.True(adapted < GigaAmAudioChunker.MaximumSilenceRms, "Тихая запись не должна упираться в потолок.");
        Assert.Equal(GigaAmAudioChunker.MaximumSilenceRms, capped, precision: 6);
    }

    [Fact]
    public void Chunk_joiner_deduplicates_boundaries_that_carry_punctuation()
    {
        // GigaAM v3 e2e emits punctuation, so the previous ordinal comparison never matched on a
        // real boundary ("три," != "три") and every long dictation kept duplicated words.
        var joined = TranscriptChunkJoiner.Join([
            new DecodedAudioChunk("раз два три,", false),
            new DecodedAudioChunk("Три, четыре пять", false)]);

        Assert.Equal("раз два три, четыре пять", joined);
    }

    [Fact]
    public void Chunk_joiner_keeps_a_phrase_the_speaker_actually_repeated()
    {
        // The overlap search used to run against the whole accumulated transcript, so a genuine
        // repetition matched a suffix from two chunks back and was deleted as a duplicate.
        var joined = TranscriptChunkJoiner.Join([
            new DecodedAudioChunk("привет мир друг", false),
            new DecodedAudioChunk("друг новый", false),
            new DecodedAudioChunk("мир друг новый", false)]);

        Assert.Equal("привет мир друг новый мир друг новый", joined);
    }

    [Fact]
    public void Chunk_joiner_closes_the_window_after_a_fully_duplicated_chunk()
    {
        // A chunk that contributes nothing leaves no boundary to match against. Treating that as
        // "no limit" would quietly restore the whole-transcript search this fix removed.
        var joined = TranscriptChunkJoiner.Join([
            new DecodedAudioChunk("альфа бета гамма", false),
            new DecodedAudioChunk("бета гамма", false),
            new DecodedAudioChunk("альфа бета гамма", false)]);

        Assert.Equal("альфа бета гамма альфа бета гамма", joined);
    }

    [Fact]
    public void Foreground_description_resolves_the_current_process_without_module_enumeration()
    {
        var described = GameForegroundPolicy.Describe((uint)Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(described.ProcessName));
        Assert.False(string.IsNullOrWhiteSpace(described.ExecutablePath));
        Assert.EndsWith(".exe", described.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Foreground_description_survives_a_process_id_that_no_longer_exists()
    {
        var described = GameForegroundPolicy.Describe(uint.MaxValue - 4);

        Assert.Equal(string.Empty, described.ProcessName);
        Assert.False(described.IsGame);
    }

    [Fact]
    public void Gigaam_service_dispose_is_idempotent()
    {
        var service = new GigaAmTranscriptionService(new IdleModelManager());

        service.Dispose();
        service.Dispose();
    }

    private static float[] LoudSignal(int length) => Constant(length, 0.5f);

    private static float[] Constant(int length, float amplitude)
    {
        var samples = new float[length];
        for (var index = 0; index < length; index++)
        {
            samples[index] = index % 2 == 0 ? amplitude : -amplitude;
        }
        return samples;
    }

    private static void Silence(float[] samples, int offset, int length) =>
        samples.AsSpan(offset, length).Clear();

    private static ArraySegment<float> SegmentOf(GigaAmAudioChunk chunk)
    {
        Assert.True(MemoryMarshal.TryGetArray(chunk.Samples, out var segment));
        Assert.InRange(segment.Count, 1, MaxChunkSamples);
        return segment;
    }

    /// <summary>A manager that is never asked for anything: the test only exercises teardown.</summary>
    private sealed class IdleModelManager : IModelManager
    {
        public event EventHandler<ModelTransferProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ModelDescriptor> RequiredModels => [];
        public bool AreAllModelsReady => true;
        public ModelTransferProgress? CurrentProgress => null;

        public Task<string> EnsureModelAsync(
            ModelDescriptor descriptor,
            IProgress<ModelTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DownloadRequiredModelsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
