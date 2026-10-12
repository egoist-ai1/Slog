using Egoist.Voice.Core;

namespace Egoist.Voice.Tests;

public sealed class GigaAmPlainRnntProfileTests
{
    [Fact]
    public void CompactProfileSelectsOnlyHashPinnedPlainRnntComponents()
    {
        var models = ModelCatalog.CreateCompactModels();
        Assert.Equal(4, models.Count);
        Assert.Equal(323_768_215L, models.Sum(model => model.SizeBytes));
        Assert.Equal(4, models.Select(model => model.Id).Distinct().Count());
        Assert.All(models, model =>
        {
            Assert.False(model.Optional);
            Assert.DoesNotContain("e2e", model.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("e2e", model.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/6888903da215c7735f51101d939f3bfa679fb2b8/", model.DownloadUri.AbsoluteUri);
            Assert.Matches("^[a-f0-9]{64}$", model.Sha256);
        });
        Assert.DoesNotContain(ModelCatalog.GigaAmTokenizer, models);
    }

    [Fact]
    public void CharacterModelCannotEnableTheHistoricalSentencePieceContext()
    {
        var config = RussianAsrProfile.CreateRecognizerConfig("encoder", "decoder", "joiner", "tokens", 8);
        Assert.False(RussianAsrProfile.ContextualBiasSupported);
        Assert.True(string.IsNullOrEmpty(config.ModelConfig.BpeVocab));
        Assert.True(string.IsNullOrEmpty(config.HotwordsFile));
        Assert.Equal(16_000, config.FeatConfig.SampleRate);
        Assert.Equal(64, config.FeatConfig.FeatureDim);
        Assert.Equal("cpu", config.ModelConfig.Provider);
        Assert.Equal("greedy_search", config.DecodingMethod);
        Assert.Equal(8, config.ModelConfig.NumThreads);
    }

    [Fact]
    public void ShippedVocabularyPreservesSpaceAtZeroAndFinalBlankWithoutBpe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Egoist.Voice.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "tests", "Egoist.Voice.Tests", "Fixtures", "plain-rnnt-tokens.txt");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Equal(ModelCatalog.GigaAmTokens.Sha256, hash);
        var tokens = File.ReadAllLines(path).Select(line =>
        {
            var separator = line.LastIndexOf(' ');
            return (Symbol: line[..separator], Id: int.Parse(line[(separator + 1)..]));
        }).ToArray();
        Assert.Equal(34, tokens.Length);
        Assert.Equal(Enumerable.Range(0, 34), tokens.Select(token => token.Id));
        Assert.Equal(" ", tokens[0].Symbol);
        Assert.Equal("<blk>", tokens[33].Symbol);
        Assert.All(tokens.Skip(1).Take(32), token =>
        {
            Assert.Single(token.Symbol);
            Assert.InRange(token.Symbol[0], 'а', 'я');
        });
        Assert.Contains(tokens, token => token.Symbol == "ь");
        Assert.Contains(tokens, token => token.Symbol == "щ");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    [InlineData(16, 8)]
    [InlineData(64, 8)]
    public void DefaultThreadBudgetIsBoundedForAudioAndUi(int logicalCores, int expectedThreads) =>
        Assert.Equal(expectedThreads, RussianAsrProfile.GetDefaultThreads(logicalCores));
}