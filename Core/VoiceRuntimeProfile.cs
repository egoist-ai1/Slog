using System.IO;
using Egoist.Voice.Services;

namespace Egoist.Voice.Core;

/// <summary>The portable marker is shipped with the CPU/Russian payload; installed builds are unchanged.</summary>
public static class VoiceRuntimeProfile
{
    public const string PortableMarker = "egoist-voice.portable";
#if COMPACT_PORTABLE
    public static bool IsPortable => true;
#else
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));
#endif
    public static string DataRoot => Environment.GetEnvironmentVariable("EGOIST_VOICE_DATA_ROOT") is { Length: > 0 } diagnosticRoot
        ? Path.GetFullPath(diagnosticRoot)
        : IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgoistVoice");
    public static string ModelsRoot => Environment.GetEnvironmentVariable("EGOIST_VOICE_MODELS_ROOT") is { Length: > 0 } diagnosticModels
        ? Path.GetFullPath(diagnosticModels)
        : IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Models")
        : Path.Combine(DataRoot, "Models");
    public static IReadOnlyList<ModelDescriptor> Models => IsPortable
        ? ModelCatalog.CreateWhisperRussianModels() : ModelCatalog.CreateRequiredModels();
    public static string Label => IsPortable ? "Русская речь · Whisper Turbo · офлайн" : "Полная версия · GigaAM + Whisper";
    public static ITranscriptionService CreateTranscription(IModelManager manager) => IsPortable
        ? new WhisperRussianService(manager) : new HybridTranscriptionService(manager);
}
