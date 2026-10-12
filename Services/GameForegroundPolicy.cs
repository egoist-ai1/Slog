using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Egoist.Voice.Services;

internal static class GameForegroundPolicy
{
    private static readonly HashSet<string> KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dota2", "cs2", "csgo", "valorant-win64-shipping", "league of legends",
        "r5apex", "fortniteclient-win64-shipping", "tslgame", "escapefromtarkov",
        "overwatch", "wow", "wowclassic", "gta5", "cod", "blackopscoldwar",
        "modernwarfare", "eldenring", "destiny2", "rainbowsix", "rainbowsix_vulkan"
    };

    private static readonly System.Text.RegularExpressions.Regex EaSportsFcName =
        new(@"^fc\d{2}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly string[] GamePathMarkers =
    [
        @"\steamapps\common\",
        @"\epic games\",
        @"\riot games\",
        @"\xboxgames\",
        @"\gog galaxy\games\",
        @"\battle.net\",
        @"\ea games\",
        @"\origin games\"
    ];

    internal static bool IsGame(string processName, string? executablePath)
    {
        var normalizedName = Path.GetFileNameWithoutExtension(processName);
        // EA Sports FC: FC25, FC26 и далее.
        if (KnownGameProcesses.Contains(normalizedName) || EaSportsFcName.IsMatch(normalizedName))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        return GamePathMarkers.Any(marker =>
            executablePath.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static ForegroundApplication GetForegroundApplication()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
        {
            return ForegroundApplication.Unknown;
        }

        return Describe(processId);
    }

    /// <summary>
    /// Resolves the image path through QueryFullProcessImageName rather than
    /// <see cref="Process.MainModule"/>. MainModule enumerates every module of a foreign process
    /// and costs tens of milliseconds; this call is a single query against the kernel and returns
    /// in well under a millisecond, which is what makes a synchronous answer affordable.
    /// </summary>
    internal static ForegroundApplication Describe(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return DescribeByNameOnly(processId);
        }

        try
        {
            var buffer = new char[1024];
            var length = buffer.Length;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref length) || length <= 0)
            {
                return DescribeByNameOnly(processId);
            }

            var path = new string(buffer, 0, length);
            return new ForegroundApplication(Path.GetFileNameWithoutExtension(path), path);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Protected and elevated processes deny the image query but still expose a name.</summary>
    private static ForegroundApplication DescribeByNameOnly(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new ForegroundApplication(process.ProcessName, null);
        }
        catch
        {
            return ForegroundApplication.Unknown;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed record ForegroundApplication(string ProcessName, string? ExecutablePath)
{
    internal static ForegroundApplication Unknown { get; } = new(string.Empty, null);
    internal bool IsGame => GameForegroundPolicy.IsGame(ProcessName, ExecutablePath);
}

/// <summary>
/// Keeps a cached answer to "is a game in the foreground?" so the mouse hook can read it as a
/// single volatile field. Foreground changes arrive through a WinEvent hook and are resolved on
/// the thread pool; results are memoized per process id.
/// </summary>
internal sealed class ForegroundGameMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly WinEventProc _callback;
    private nint _hook;
    private volatile bool _isGame;
    private volatile string _processName = string.Empty;
    private uint _currentProcessId;
    private bool _disposed;

    internal ForegroundGameMonitor()
    {
        _callback = OnForegroundChanged;
    }

    internal bool ForegroundIsGame => _isGame;
    internal string ForegroundProcessName => _processName;

    /// <summary>Must be called from a thread that pumps messages: WinEvent hooks are delivered there.</summary>
    internal void Start()
    {
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            _callback,
            0,
            0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        if (_hook == 0)
        {
            AppLog.Write("Foreground monitor unavailable; falling back to per-press resolution");
        }

        Refresh(NativeMethods.GetForegroundWindow());
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventId,
        nint window,
        int objectId,
        int childId,
        uint threadId,
        uint eventTime) => Refresh(window);

    private void Refresh(nint window)
    {
        if (_disposed || window == 0)
        {
            return;
        }

        if (GameForegroundPolicy.GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
        {
            return;
        }

        if (processId == Volatile.Read(ref _currentProcessId))
        {
            return;
        }

        Volatile.Write(ref _currentProcessId, processId);

        // Resolved synchronously and without caching. QueryFullProcessImageName is cheap enough
        // for the message pump, and skipping the cache removes two failure modes at once: a stale
        // answer during the first press after an Alt-Tab, and Windows reusing a process id that a
        // game held earlier — which would either kill the trigger in an ordinary application or,
        // worse, fire it inside a game.
        Apply(GameForegroundPolicy.Describe(processId));
    }

    private void Apply(ForegroundApplication application)
    {
        _processName = application.ProcessName;
        _isGame = application.IsGame;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    private delegate void WinEventProc(
        nint hook,
        uint eventId,
        nint window,
        int objectId,
        int childId,
        uint threadId,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
