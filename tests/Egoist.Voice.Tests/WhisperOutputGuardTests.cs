using Egoist.Voice.Services;
using Xunit;

namespace Egoist.Voice.Tests;

public class WhisperOutputGuardTests
{
    [Theory]
    [InlineData("Субтитры сделал DimaTorzok")]
    [InlineData("Редактор субтитров А.Семкин Корректор А.Егорова")]
    [InlineData("Продолжение следует...")]
    [InlineData("  ")]
    public void Known_silence_hallucinations_are_dropped(string text) =>
        Assert.Equal(string.Empty, WhisperOutputGuard.Clean(text));

    [Fact]
    public void Ordinary_dictation_is_untouched() =>
        Assert.Equal("Закоммить это и запушь в GitHub, только сначала прогони тесты.",
            WhisperOutputGuard.Clean("Закоммить это и запушь в GitHub, только сначала прогони тесты."));

    [Fact]
    public void Runaway_repetition_is_cut_to_three_repeats() =>
        Assert.Equal("Хорошо да да да",
            WhisperOutputGuard.Clean("Хорошо да да да да да да да да да да"));

    [Fact]
    public void Short_emphasis_is_kept() =>
        Assert.Equal("да да да нет", WhisperOutputGuard.Clean("да да да нет"));
}
