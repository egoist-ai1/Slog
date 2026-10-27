using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace Egoist.Voice.Services;

public enum ActivationBinding
{
    Mouse5AndKeyboard,
    Mouse5,
    Mouse4,
    Keyboard,
    CustomKeyboard
}

public enum MouseSideButton
{
    Mouse4 = 1,
    Mouse5 = 2
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public readonly record struct KeyboardShortcut(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static KeyboardShortcut Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20);

    public bool IsValid =>
        VirtualKey is > 0 and < 0x100 &&
        (Modifiers & ~(HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows)) == 0 &&
        VirtualKey is not 0x10 and not 0x11 and not 0x12 and not 0x5B and not 0x5C &&
        (Modifiers != HotkeyModifiers.None || IsSafeStandaloneKey(VirtualKey));

    public string DisplayName
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
            parts.Add(KeyName(VirtualKey));
            return string.Join(" + ", parts);
        }
    }

    public static KeyboardShortcut FromKey(Key key, ModifierKeys modifiers)
    {
        var normalizedModifiers = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) normalizedModifiers |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) normalizedModifiers |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) normalizedModifiers |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) normalizedModifiers |= HotkeyModifiers.Windows;
        return new KeyboardShortcut(normalizedModifiers, KeyInterop.VirtualKeyFromKey(key));
    }

    internal IEnumerable<int> HeldVirtualKeys()
    {
        yield return VirtualKey;
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) yield return 0x11;
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) yield return 0x12;
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) yield return 0x10;
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            yield return 0x5B;
            yield return 0x5C;
        }
    }

    private static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }
        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            _ => KeyInterop.KeyFromVirtualKey(virtualKey).ToString()
        };
    }

    private static bool IsSafeStandaloneKey(int virtualKey) =>
        virtualKey is >= 0x70 and <= 0x87 or // F1-F24
        0x13 or 0x91 or                     // Pause, Scroll Lock
        >= 0xA6 and <= 0xB7;                // Browser and media keys
}

public sealed record ActivationConfiguration(
    ActivationBinding Binding,
    KeyboardShortcut? CustomShortcut = null)
{
    public static ActivationConfiguration Default => new(ActivationBinding.Mouse5AndKeyboard);

    public ActivationConfiguration WithBinding(ActivationBinding binding) => this with { Binding = binding };
}

public static class ActivationBindingInfo
{
    public static bool UsesKeyboard(ActivationBinding binding) =>
        binding is ActivationBinding.Keyboard or ActivationBinding.Mouse5AndKeyboard or ActivationBinding.CustomKeyboard;

    public static KeyboardShortcut? Keyboard(ActivationConfiguration configuration) => configuration.Binding switch
    {
        ActivationBinding.Keyboard or ActivationBinding.Mouse5AndKeyboard => KeyboardShortcut.Default,
        ActivationBinding.CustomKeyboard => configuration.CustomShortcut,
        _ => null
    };

    public static MouseSideButton? MouseButton(ActivationBinding binding) => binding switch
    {
        ActivationBinding.Mouse5AndKeyboard or ActivationBinding.Mouse5 => MouseSideButton.Mouse5,
        ActivationBinding.Mouse4 => MouseSideButton.Mouse4,
        _ => null
    };

    public static string DisplayName(ActivationBinding binding) => binding switch
    {
        ActivationBinding.Mouse5AndKeyboard => "Mouse 5 + Ctrl + Alt + Space",
        ActivationBinding.Mouse5 => "Mouse 5",
        ActivationBinding.Mouse4 => "Mouse 4",
        ActivationBinding.Keyboard => "Ctrl + Alt + Space",
        ActivationBinding.CustomKeyboard => "Задать свою…",
        _ => binding.ToString()
    };

    /// <summary>
    /// Текст о том, что запрошенная привязка не подключилась и работает только запасная кнопка.
    /// Содержит лишь имя привязки и причину, без путей и данных пользователя.
    /// </summary>
    public static string FallbackNotice(string requestedName, string? error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "причина неизвестна" : error.Trim().TrimEnd('.', ' ');
        return $"{requestedName} не подключилось: {reason}. Пока работает только запасная кнопка.";
    }

    public static string DisplayName(ActivationConfiguration configuration) =>
        configuration.Binding == ActivationBinding.CustomKeyboard && configuration.CustomShortcut is { IsValid: true } custom
            ? custom.DisplayName
            : DisplayName(configuration.Binding);
}

internal sealed record ActivationSettings(
    ActivationBinding Binding,
    KeyboardShortcut? CustomShortcut = null);

internal sealed class ActivationSettingsService
{
    private readonly string _settingsPath;

    internal ActivationSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Egoist.Voice.Core.VoiceRuntimeProfile.DataRoot,
            "activation.json");
    }

    internal ActivationConfiguration Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return ActivationConfiguration.Default;
            }

            var settings = JsonSerializer.Deserialize<ActivationSettings>(File.ReadAllText(_settingsPath));
            if (settings is null || !Enum.IsDefined(settings.Binding))
            {
                return ActivationConfiguration.Default;
            }

            if (settings.Binding == ActivationBinding.CustomKeyboard && settings.CustomShortcut is not { IsValid: true })
            {
                AppLog.Write("Invalid custom activation shortcut; using the safe default");
                return ActivationConfiguration.Default;
            }

            return new ActivationConfiguration(settings.Binding, settings.CustomShortcut);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not load activation settings", exception);
            return ActivationConfiguration.Default;
        }
    }

    internal void Save(ActivationConfiguration configuration)
    {
        if (configuration.Binding == ActivationBinding.CustomKeyboard && configuration.CustomShortcut is not { IsValid: true })
        {
            throw new InvalidDataException("Пользовательская горячая клавиша не задана.");
        }

        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            new ActivationSettings(configuration.Binding, configuration.CustomShortcut)));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
