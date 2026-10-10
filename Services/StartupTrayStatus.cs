using System.Drawing;
using Forms = System.Windows.Forms;

namespace Egoist.Voice.Services;

/// <summary>
/// Временная иконка трея на время ожидания микрофона при старте (окна и основного трея ещё нет).
/// Создаётся лениво — только если первая попытка не удалась.
/// </summary>
internal sealed class StartupTrayStatus : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private bool _detached;

    internal void ShowWaiting(int retry) =>
        Show($"Слог — ожидаю микрофон (попытка {retry} из {MicrophoneStartupRetry.MaximumRetries})", null);

    internal void ShowFailed(Action exit) =>
        Show("Слог — микрофон недоступен. Выход: правый клик", exit);

    /// <summary>Передаёт владение иконкой вызывающему: Dispose этого объекта её уже не закроет.</summary>
    internal IDisposable Detach()
    {
        _detached = true;
        return new Owner(_icon);
    }

    private void Show(string text, Action? exit)
    {
        try
        {
            if (_icon is null)
            {
                Icon? icon = null;
                try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""); } catch { }
                _icon = new Forms.NotifyIcon { Icon = icon ?? SystemIcons.Application, Visible = true };
            }
            _icon.Text = text.Length > 63 ? text[..63] : text;
            if (exit is not null)
            {
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Выход", null, (_, _) => exit());
                _icon.ContextMenuStrip = menu;
            }
        }
        catch (Exception exception) { AppLog.Write("Startup tray status failed", exception); }
    }

    public void Dispose()
    {
        if (!_detached) Owner.Close(_icon);
    }

    private sealed class Owner(Forms.NotifyIcon? icon) : IDisposable
    {
        public void Dispose() => Close(icon);

        internal static void Close(Forms.NotifyIcon? target)
        {
            if (target is null) return;
            target.Visible = false;
            target.Dispose();
        }
    }
}
