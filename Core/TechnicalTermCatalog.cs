namespace Egoist.Voice.Core;

internal static class TechnicalTermCatalog
{
    /// <summary>
    /// Terms the candidate selector treats as evidence that the fallback engine preserved English
    /// the primary one lost. Kept broad but not endless: every entry here is a word that a Russian
    /// speaker routinely says in Latin script mid-sentence.
    /// </summary>
    internal static readonly IReadOnlyList<string> Terms = BuiltInVocabulary.Terms
        .Select(term => term.Written)
        .Where(term => term.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        .Concat(
        [
            "API", "Arc", "Bash", "branch", "cache", "CHANGELOG", "Chrome", "CI", "CLI", "commit",
            "CUDA", "deploy", "dependency injection", "desktop", "Discord", "Egoist Shield", "Egoist Voice",
            "Excel", "exception", "framework",
            "Git", "GitHub Copilot", "Google Chrome", "health check", "issue", "LICENSE", "logs",
            "main", "Neon", "overlay", "package", "Paper", "pipeline", "plugin", "pod", "Radeon",
            "README", "Redis", "REST", "rollback", "serverless", "signature", "Steam", "Swift",
            "Telegram", "texture", "Vite", "webhook", "Windows 11"
        ])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(term => term.Length)
        .ToArray();

}
