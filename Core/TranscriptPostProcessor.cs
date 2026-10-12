namespace Egoist.Voice.Core;

public sealed record PostProcessingOptions(
    bool ApplyDictionary = true,
    bool ApplyVoiceCommands = true,
    bool ApplyNumberNormalization = false,
    bool PreserveSpokenWords = false)
{
    /// <summary>
    /// Number normalization is off by default on purpose. It is the one step that changes text the
    /// user did not ask to change — dictating prose, "двадцать пять" should stay words — so it is
    /// opt-in, unlike the dictionary and the formatting commands.
    /// </summary>
    public static PostProcessingOptions Default { get; } = new();
}

/// <summary>
/// The single deterministic pipeline applied to whichever engine won. Previously each engine had
/// its own formatting path, so the same speech came out differently depending on which one the
/// selector happened to pick.
/// </summary>
public sealed class TranscriptPostProcessor
{
    private readonly Lazy<UserDictionary> _dictionary;
    private readonly Lazy<VoiceCommandProcessor> _commands;

    public TranscriptPostProcessor(
        UserDictionary? dictionary = null,
        PostProcessingOptions? options = null,
        VoiceCommandProcessor? commands = null)
        : this(new Lazy<UserDictionary>(dictionary ?? UserDictionary.Empty), options, commands)
    {
    }

    /// <summary>
    /// Takes the dictionary lazily: building it compiles over a thousand regular expressions, and
    /// verbatim mode (<see cref="PostProcessingOptions.PreserveSpokenWords"/>) never reads it, so
    /// start-up should not pay for it until a dictation actually needs it.
    /// </summary>
    public TranscriptPostProcessor(
        Lazy<UserDictionary> dictionary,
        PostProcessingOptions? options = null,
        VoiceCommandProcessor? commands = null)
    {
        _dictionary = dictionary;
        Options = options ?? PostProcessingOptions.Default;
        _commands = commands is null
            ? new Lazy<VoiceCommandProcessor>(() => new VoiceCommandProcessor())
            : new Lazy<VoiceCommandProcessor>(commands);
    }

    public UserDictionary Dictionary => _dictionary.Value;
    public PostProcessingOptions Options { get; }

    public string Process(string text, EntityProfile entityProfile = EntityProfile.General)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (Options.PreserveSpokenWords)
        {
            return TrustedSpellingCorrections.Apply(text.Trim());
        }

        // Order matters. The dictionary runs first so its replacements are visible to the command
        // pass; numbers run before the commands so that "пять процентов, точка" resolves cleanly;
        // whitespace and capitalization are tidied last, over the final shape of the text.
        if (Options.ApplyDictionary)
        {
            text = _dictionary.Value.Apply(text, entityProfile);
        }

        if (Options.ApplyNumberNormalization)
        {
            text = RussianNumberNormalizer.Normalize(text);
        }

        if (Options.ApplyVoiceCommands)
        {
            text = _commands.Value.Apply(text);
        }

        return TranscriptNormalizer.Normalize(text);
    }
}
