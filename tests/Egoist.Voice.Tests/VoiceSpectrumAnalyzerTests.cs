using System.Diagnostics;
using Egoist.Voice.Controls;
using Egoist.Voice.Services;
using NAudio.Wave;
using Xunit.Abstractions;

namespace Egoist.Voice.Tests;

public sealed class VoiceSpectrumAnalyzerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(120, 0)]
    [InlineData(250, 1)]
    [InlineData(500, 2)]
    [InlineData(850, 3)]
    [InlineData(1400, 4)]
    [InlineData(2200, 5)]
    [InlineData(3300, 6)]
    [InlineData(5500, 7)]
    public void Actual_frequency_selects_its_own_band(int frequency, int expectedBand)
    {
        var spectrum = new VoiceSpectrumAnalyzer().Measure(Tone(frequency), 8192,
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        Assert.True(spectrum.IsMeasured);
        var strongest = Enumerable.Range(0, 8).MaxBy(index => spectrum[index]);
        Assert.Equal(expectedBand, strongest);
        Assert.InRange(spectrum[expectedBand], 0.2f, 1);
    }

    [Fact]
    public void Analyzer_preserves_source_and_reset_removes_previous_sound()
    {
        var analyzer = new VoiceSpectrumAnalyzer();
        var bytes = Tone(500);
        var copy = bytes.ToArray();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        analyzer.Measure(bytes, bytes.Length, format);
        Assert.Equal(copy, bytes);
        analyzer.Reset();
        var silence = analyzer.Measure(new byte[8192], 8192, format);
        for (var band = 0; band < 8; band++) Assert.Equal(0, silence[band]);
    }

    [Fact]
    public void Edges_remain_thin_even_at_full_level_and_each_band_changes_its_bar()
    {
        var full = new VoiceSpectrum(1, 1, 1, 1, 1, 1, 1, 1);
        var edge = CapsuleWaveformProfile.TargetScale(0, 15, 1, 0, false, spectrum: full);
        var centre = CapsuleWaveformProfile.TargetScale(7, 15, 1, 0, false, spectrum: full);
        Assert.True(edge < centre * 0.15);
        Assert.Equal(edge, CapsuleWaveformProfile.TargetScale(14, 15, 1, 0, false, spectrum: full));
        var hollow = new VoiceSpectrum(1, 1, 0, 1, 1, 1, 1, 1);
        Assert.True(CapsuleWaveformProfile.TargetScale(5, 15, 1, 0, false, spectrum: hollow)
            < CapsuleWaveformProfile.TargetScale(5, 15, 1, 0, false, spectrum: full) * 0.3);
    }

    [Fact]
    public void Spectrum_reuses_its_buffers_for_repeated_capture_frames()
    {
        var analyzer = new VoiceSpectrumAnalyzer();
        var bytes = Tone(850);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        // Прогрев с запасом: 20 вызовов меньше порога многоуровневой JIT-компиляции (30), и рантайм
        // мог выполнить повышение уровня и связанные с ним выделения прямо внутри измеряемого цикла.
        // Устойчивое состояние измеряем несколькими раундами и берём минимум: регрессия с выделением
        // на кадр аллоцирует в каждом раунде, разовый шум рантайма (JIT, параллельные тесты) — нет.
        for (var i = 0; i < 300; i++) analyzer.Measure(bytes, bytes.Length, format);
        var allocated = long.MaxValue;
        var clock = Stopwatch.StartNew();
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) analyzer.Measure(bytes, bytes.Length, format);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        clock.Stop();
        output.WriteLine($"5000 FFT frames: {clock.Elapsed.TotalMilliseconds:F2} ms; minimum managed allocations per 1000 frames: {allocated} bytes.");
        Assert.InRange(allocated, 0, 1024);
    }

    private static byte[] Tone(int frequency)
    {
        var bytes = new byte[8192];
        for (var frame = 0; frame < 1024; frame++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * frequency * frame / 48000) * 0.02);
            BitConverter.TryWriteBytes(bytes.AsSpan(frame * 8), sample);
            BitConverter.TryWriteBytes(bytes.AsSpan(frame * 8 + 4), sample);
        }
        return bytes;
    }
}
