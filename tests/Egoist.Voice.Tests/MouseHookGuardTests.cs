namespace Egoist.Voice.Tests;

/// <summary>
/// Хук глушит события боковых кнопок, поэтому GetAsyncKeyState их не видит. Проверка «система тоже
/// не видит кнопку нажатой» всегда проходила бы и только создавала иллюзию защиты.
/// </summary>
public sealed class MouseHookGuardTests
{
    [Fact]
    public void Lost_release_guard_relies_on_the_confirmation_streak_not_on_async_key_state()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Services", "MousePushToTalkService.cs"));

        Assert.DoesNotContain("static extern short GetAsyncKeyState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPhysicallyDown", source, StringComparison.Ordinal);
        Assert.Contains("_suspectStreak < 2", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Egoist.Voice.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
