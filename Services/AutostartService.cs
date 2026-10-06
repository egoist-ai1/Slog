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
