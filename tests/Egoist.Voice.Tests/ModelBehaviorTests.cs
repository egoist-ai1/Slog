using Egoist.Voice.Core;
using Egoist.Voice.Services;
using System.Runtime.InteropServices;

namespace Egoist.Voice.Tests;

public sealed class ModelBehaviorTests
{
    [Theory]
    [InlineData("Распознаю")]
    [InlineData("Распознаю 2/3")]
    [InlineData("  распознаю 48%")]
    public void RecognitionProgressPolicyHidesEngineChunkDetails(string label)
    {
        Assert.False(RecognitionProgressPolicy.ShouldRenderEngineProgress(label));
    }

    [Theory]
    [InlineData("Запускаю GigaAM…")]
    [InlineData("Проверяю модель")]
    [InlineData("Загружаю 42%")]
    public void RecognitionProgressPolicyKeepsModelLifecycleProgress(string label)
    {
        Assert.True(RecognitionProgressPolicy.ShouldRenderEngineProgress(label));
    }

    [Fact]
    public void GigaAmChunkerKeepsAllSamplesAndLimitsChunkLength()
    {
        const int sampleRate = 100;
        const int maxSeconds = 22;
        var samples = Enumerable.Range(0, 5_500).Select(value => (float)value).ToArray();

        var chunks = GigaAmAudioChunker.Split(samples, sampleRate, maxSeconds);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Samples.Length, 1, maxSeconds * sampleRate));
        Assert.All(chunks, chunk =>
        {
            Assert.True(MemoryMarshal.TryGetArray(chunk.Samples, out var segment));
            Assert.Same(samples, segment.Array);
        });

        var defaultChunks = GigaAmAudioChunker.Split(samples, sampleRate);
        Assert.True(defaultChunks.Count >= 2);
        Assert.All(defaultChunks, chunk => Assert.InRange(chunk.Samples.Length, 1, GigaAmAudioChunker.DefaultMaxSeconds * sampleRate));
    }

    [Fact]
    public void ChunkJoinerRemovesOnlyExactBoundaryDuplication()
    {
        var joined = TranscriptChunkJoiner.Join([
            new DecodedAudioChunk("один два три", false),
            new DecodedAudioChunk("два три четыре", false)]);

        Assert.Equal("один два три четыре", joined);
    }

    [Fact]
    public void ChunkJoinerStartsParagraphOnlyAfterConfirmedLongPause()
    {
        var joined = TranscriptChunkJoiner.Join([
            new DecodedAudioChunk("первая мысль", false),
            new DecodedAudioChunk("вторая мысль", true)]);

        Assert.Equal($"первая мысль{Environment.NewLine}{Environment.NewLine}вторая мысль", joined);
    }

    [Fact]
    public void Formats_compact_and_detailed_progress()
    {
        var progress = new ModelTransferProgress(
            "Whisper Large v3 Turbo", 1, 1, ModelTransferStage.Downloading,
            245_000_000, 574_041_195, 42.7, 42.7, 38_000_000, TimeSpan.FromSeconds(9));

        Assert.Contains("43%", ModelProgressFormatter.Capsule(progress));
        Assert.Contains("38 МБ/с", ModelProgressFormatter.Detail(progress));
        Assert.Contains("~0:09", ModelProgressFormatter.Detail(progress));
        Assert.True(ModelProgressFormatter.TrayTooltip(progress).Length <= 63);
    }
}
