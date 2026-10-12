using Egoist.Voice.Core;
using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class GigaAmFormattingProfileTests
{
    [Theory]
    [InlineData("gigaam_v3_e2e_rnnt_encoder_int8.onnx", 318_995_997L, "2cac62d0c270bd128f898f2be1a2d34780d524a6e9483888ebac7b00f97410f1")]
    [InlineData("gigaam_v3_e2e_rnnt_decoder.onnx", 4_600_058L, "781971998e6a355d6a714f6932a30eab295e7ba0d14fd7e0f78c83b87e811860")]
    [InlineData("gigaam_v3_e2e_rnnt_joint.onnx", 2_712_896L, "602ff7017a93311aad34df1437c8d7f49911353c13d6eae7a6ee7b041339465c")]
    [InlineData("gigaam_v3_e2e_rnnt_tokens.txt", 13_353L, "7ddf22514c42c531358182c81446a8159771e9921019f09ae743ea622d40221d")]
    public void FormattingModelsPinTheVerifiedE2eFiles(string file, long bytes, string hash)
    {
        var descriptor = Assert.Single(ModelCatalog.CreateFormattingModels(), model => model.FileName == file);
        Assert.Equal(bytes, descriptor.SizeBytes);
        Assert.Equal(hash, descriptor.Sha256);
        Assert.Contains("/6888903da215c7735f51101d939f3bfa679fb2b8/", descriptor.DownloadUri.AbsoluteUri);
        Assert.Contains("e2e", descriptor.Id);
        Assert.False(descriptor.Optional);
    }

    [Fact]
    public void QualityAssetsCombineDistinctModelSetsWithoutChangingDefaults()
    {
        var plain = ModelCatalog.CreateCompactModels();
        var formatting = ModelCatalog.CreateFormattingModels();
        var quality = ModelCatalog.CreateRussianQualityModels();
        Assert.Equal(4, plain.Count);
        Assert.Equal(4, formatting.Count);
        Assert.Equal(8, quality.Count);
        Assert.Equal(650_090_519L, quality.Sum(model => model.SizeBytes));
        Assert.Equal(8, quality.Select(model => model.Id).Distinct().Count());
        Assert.Equal(plain.Concat(formatting), quality);
        Assert.DoesNotContain(ModelCatalog.GigaAmTokenizer, quality);
    }

    [Fact]
    public void PublicConstructorStillSelectsPlainPathsAndDefaultThreadBudget()
    {
        using var manager = new RecordingModelManager();
        using var engine = new GigaAmTranscriptionService(manager, enableContextualBias: true);
        var config = engine.CreateRecognizerConfiguration(Paths(ModelCatalog.CreateRussianQualityModels()));
        Assert.Equal("GigaAM", engine.EngineName);
        Assert.Equal(ModelCatalog.GigaAmEncoder.FileName, config.ModelConfig.Transducer.Encoder);
        Assert.Equal(ModelCatalog.GigaAmTokens.FileName, config.ModelConfig.Tokens);
        Assert.Equal(GigaAmTranscriptionService.BenchmarkDecodeThreads, config.ModelConfig.NumThreads);
        Assert.False(engine.ContextualBiasActive);
        Assert.True(string.IsNullOrEmpty(config.ModelConfig.BpeVocab));
        Assert.True(string.IsNullOrEmpty(config.HotwordsFile));
    }

    [Fact]
    public void FormattingFactorySelectsE2ePathsAndMeasuredGreedyConfiguration()
    {
        using var manager = new RecordingModelManager();
        using var engine = GigaAmTranscriptionService.CreateFormattingEngine(manager);
        var config = engine.CreateRecognizerConfiguration(Paths(ModelCatalog.CreateRussianQualityModels()));
        Assert.Equal("GigaAM v3 E2E RNNT", engine.EngineName);
        Assert.Equal(ModelCatalog.GigaAmE2eEncoder.FileName, config.ModelConfig.Transducer.Encoder);
        Assert.Equal(ModelCatalog.GigaAmE2eDecoder.FileName, config.ModelConfig.Transducer.Decoder);
        Assert.Equal(ModelCatalog.GigaAmE2eJoiner.FileName, config.ModelConfig.Transducer.Joiner);
        Assert.Equal(ModelCatalog.GigaAmE2eTokens.FileName, config.ModelConfig.Tokens);
        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 4), config.ModelConfig.NumThreads);
        Assert.Equal("greedy_search", config.DecodingMethod);
        Assert.Equal("cpu", config.ModelConfig.Provider);
        Assert.Equal(64, config.FeatConfig.FeatureDim);
        Assert.False(engine.ContextualBiasActive);
        Assert.True(string.IsNullOrEmpty(config.ModelConfig.BpeVocab));
        Assert.True(string.IsNullOrEmpty(config.HotwordsFile));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(20, 12)]
    public void FormattingExplicitThreadOverrideUsesExistingSafeBounds(int requested, int expected)
    {
        using var manager = new RecordingModelManager();
        using var engine = GigaAmTranscriptionService.CreateFormattingEngine(manager, requested);
        Assert.Equal(expected, engine.CreateRecognizerConfiguration(Paths(ModelCatalog.CreateFormattingModels())).ModelConfig.NumThreads);
    }

    [Fact]
    public void FormattingConfigurationCannotAccidentallyUseThePlainModelPaths()
    {
        using var manager = new RecordingModelManager();
        using var engine = GigaAmTranscriptionService.CreateFormattingEngine(manager);
        Assert.Throws<KeyNotFoundException>(() => engine.CreateRecognizerConfiguration(Paths(ModelCatalog.CreateCompactModels())));
    }

    [Fact]
    public async Task FormattingWarmupRequestsOnlyItsOwnFourComponents()
    {
        using var manager = new RecordingModelManager(failAfter: 4);
        using var engine = GigaAmTranscriptionService.CreateFormattingEngine(manager);
        await Assert.ThrowsAsync<ModelTransferStoppedException>(() => engine.WarmUpAsync(null, CancellationToken.None));
        Assert.Equal(ModelCatalog.CreateFormattingModels().Select(model => model.Id), manager.Requested.Select(model => model.Id));
        Assert.False(engine.ContextualBiasActive);
    }

    [Fact]
    public void FormattingEngineDoesNotDisposeTheSharedModelManager()
    {
        using var manager = new RecordingModelManager();
        GigaAmTranscriptionService.CreateFormattingEngine(manager).Dispose();
        Assert.False(manager.Disposed);
        Assert.Throws<ArgumentNullException>(() => GigaAmTranscriptionService.CreateFormattingEngine(null!));
    }

    private static IReadOnlyDictionary<string, string> Paths(IReadOnlyList<ModelDescriptor> descriptors) =>
        descriptors.ToDictionary(model => model.Id, model => model.FileName);

    private sealed class ModelTransferStoppedException : Exception { }

    private sealed class RecordingModelManager(int failAfter = 0) : IModelManager
    {
        public event EventHandler<ModelTransferProgress>? ProgressChanged { add { } remove { } }
        public IReadOnlyList<ModelDescriptor> RequiredModels => ModelCatalog.CreateRussianQualityModels();
        public bool AreAllModelsReady => false;
        public ModelTransferProgress? CurrentProgress => null;
        public List<ModelDescriptor> Requested { get; } = [];
        public bool Disposed { get; private set; }
        public Task<string> EnsureModelAsync(ModelDescriptor descriptor, IProgress<ModelTransferProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.Add(descriptor);
            return Requested.Count == failAfter
                ? Task.FromException<string>(new ModelTransferStoppedException())
                : Task.FromResult(descriptor.FileName);
        }
        public Task DownloadRequiredModelsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
