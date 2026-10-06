using Egoist.Voice.Services;

namespace Egoist.Voice.Core;

public static class ModelCatalog
{
    private const string GigaAmRevision = "6888903da215c7735f51101d939f3bfa679fb2b8";
    private const string WhisperRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private const string OfficialGigaAmRevision = "ec1dc1f01d0d627ab2c0d3acc1e235702300d95e";
    private const string GigaAmBaseUri =
        "https://huggingface.co/Smirnov75/GigaAM-v3-sherpa-onnx/resolve/" + GigaAmRevision + "/";

    public static readonly ModelDescriptor GigaAmEncoder = new(
        "gigaam-v3-rnnt-int8-v1",
        "GigaAM v3 · ядро",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_rnnt_encoder_int8.onnx"),
        "gigaam_v3_rnnt_encoder_int8.onnx",
        318_995_995,
        "53138ee4241c22482a45f3f9f80b787749fa7456f29221c4652afb7a1f84b681");

    public static readonly ModelDescriptor GigaAmDecoder = new(
        "gigaam-v3-rnnt-decoder-v1",
        "GigaAM v3 · декодер",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_rnnt_decoder.onnx"),
        "gigaam_v3_rnnt_decoder.onnx",
        3_331_577,
        "633ef97f2c6c9ca11c91b6c7ee8f6054fc7a964e6b22d7a904fd096b458f0308");

    public static readonly ModelDescriptor GigaAmJoiner = new(
        "gigaam-v3-rnnt-joiner-v1",
        "GigaAM v3 · связка",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_rnnt_joint.onnx"),
        "gigaam_v3_rnnt_joint.onnx",
        1_440_448,
        "fd1d02f45c2ad3d6b67cc149811ad794ab4b020ed49a0a9e2790a8619d1cddd8");

    public static readonly ModelDescriptor GigaAmTokens = new(
        "gigaam-v3-rnnt-tokens-v1",
        "GigaAM v3 · словарь",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_rnnt_tokens.txt"),
        "gigaam_v3_rnnt_tokens.txt",
        195,
        "48c9111eb77c9c42d08ecc71c00c09407ef2cce01195d72b7cc0c3c08ce89213");

    public static readonly ModelDescriptor GigaAmE2eEncoder = new(
        "gigaam-v3-e2e-rnnt-int8-v1",
        "GigaAM v3 · оформление · ядро",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_e2e_rnnt_encoder_int8.onnx"),
        "gigaam_v3_e2e_rnnt_encoder_int8.onnx",
        318_995_997,
        "2cac62d0c270bd128f898f2be1a2d34780d524a6e9483888ebac7b00f97410f1");

    public static readonly ModelDescriptor GigaAmE2eDecoder = new(
        "gigaam-v3-e2e-rnnt-decoder-v1",
        "GigaAM v3 · оформление · декодер",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_e2e_rnnt_decoder.onnx"),
        "gigaam_v3_e2e_rnnt_decoder.onnx",
        4_600_058,
        "781971998e6a355d6a714f6932a30eab295e7ba0d14fd7e0f78c83b87e811860");

    public static readonly ModelDescriptor GigaAmE2eJoiner = new(
        "gigaam-v3-e2e-rnnt-joiner-v1",
        "GigaAM v3 · оформление · связка",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_e2e_rnnt_joint.onnx"),
        "gigaam_v3_e2e_rnnt_joint.onnx",
        2_712_896,
        "602ff7017a93311aad34df1437c8d7f49911353c13d6eae7a6ee7b041339465c");

    public static readonly ModelDescriptor GigaAmE2eTokens = new(
        "gigaam-v3-e2e-rnnt-tokens-v1",
        "GigaAM v3 · оформление · словарь",
        ModelKind.Speech,
        new Uri(GigaAmBaseUri + "gigaam_v3_e2e_rnnt_tokens.txt"),
        "gigaam_v3_e2e_rnnt_tokens.txt",
        13_353,
        "7ddf22514c42c531358182c81446a8159771e9921019f09ae743ea622d40221d");

    /// <summary>
    /// Historical E2E tokenizer retained for explicit compatibility tools. The plain RNNT
    /// production model uses 34 character tokens and must not load this SentencePiece vocabulary.
    /// </summary>
    public static readonly ModelDescriptor GigaAmTokenizer = new(
        "gigaam-v3-e2e-rnnt-tokenizer-v1",
        "GigaAM v3 · контекстный словарь",
        ModelKind.Speech,
        new Uri("https://huggingface.co/ai-sage/GigaAM-v3/resolve/" + OfficialGigaAmRevision + "/tokenizer.model"),
        "tokenizer.model",
        255_336,
        "828c12c991019eef952a960661f25a92d6ad279591e2ea466b4aeddf1d20a18a",
        Optional: true);

    public static readonly ModelDescriptor Whisper = new(
        "whisper-large-v3-turbo-q5_0-v1",
        "Whisper Large v3 Turbo",
        ModelKind.Speech,
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/" + WhisperRevision + "/ggml-large-v3-turbo-q5_0.bin"),
        "ggml-large-v3-turbo-q5_0.bin",
        574_041_195,
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2");

    public static IReadOnlyList<ModelDescriptor> CreateRequiredModels() =>
        [GigaAmEncoder, GigaAmDecoder, GigaAmJoiner, GigaAmTokens, Whisper];

    public static IReadOnlyList<ModelDescriptor> CreateCompactModels() =>
        [GigaAmEncoder, GigaAmDecoder, GigaAmJoiner, GigaAmTokens];

    public static IReadOnlyList<ModelDescriptor> CreateFormattingModels() =>
        [GigaAmE2eEncoder, GigaAmE2eDecoder, GigaAmE2eJoiner, GigaAmE2eTokens];

    public static IReadOnlyList<ModelDescriptor> CreateWhisperRussianModels() => [Whisper];

    public static IReadOnlyList<ModelDescriptor> CreateRussianQualityModels() =>
        [GigaAmEncoder, GigaAmDecoder, GigaAmJoiner, GigaAmTokens,
            GigaAmE2eEncoder, GigaAmE2eDecoder, GigaAmE2eJoiner, GigaAmE2eTokens];
}
