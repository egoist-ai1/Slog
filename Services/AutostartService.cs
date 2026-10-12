using Microsoft.Win32;

namespace Egoist.Voice.Services;

/// <summary>
/// Start-with-Windows switch. It writes the same per-user Run value the installer uses, so the two
/// stay one setting: the installer's task and this checkbox read and write one registry entry.
/// </summary>
internal static class AutostartService
{
    internal const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "EgoistVoice";

    internal static string CommandFor(string executablePath) => $"\"{executablePath}\" --background";

    internal static bool IsEnabled(string runKey = DefaultRunKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: false);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Restores the Run value when the user wants autostart but it is missing (fresh portable copy,
    /// cleaned registry, reinstall). It only fills a gap: an existing value, even one pointing
    /// elsewhere, is left alone, and a development build never registers itself.
    /// </summary>
    internal static bool EnsureEnabled(bool wanted, string? executablePath, string runKey = DefaultRunKey)
    {
        if (!wanted || string.IsNullOrEmpty(executablePath) || IsDevelopmentBuild(executablePath) || IsEnabled(runKey))
            return false;
        return SetEnabled(true, executablePath, runKey);
    }

    internal static bool IsDevelopmentBuild(string executablePath) =>
        executablePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || executablePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
        || executablePath.Contains(@"\artifacts\", StringComparison.OrdinalIgnoreCase);

    internal static bool SetEnabled(bool enabled, string executablePath, string runKey = DefaultRunKey)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            if (key is null) return false;
            if (enabled) key.SetValue(ValueName, CommandFor(executablePath), RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            AppLog.Write("Autostart change failed", exception);
            return false;
        }
    }
}
