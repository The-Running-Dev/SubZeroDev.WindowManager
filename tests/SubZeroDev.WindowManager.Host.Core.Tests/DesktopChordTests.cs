using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>S26.6: the desktop chords, and Win+Tab passing through while its plugin is not running.</summary>
public sealed class DesktopChordTests
{
    private const uint Ctrl = 0xA2;
    private const uint Left = 0x25;
    private const uint Right = 0x27;
    private const uint Tab = 0x09;
    private const uint F4 = 0x73;

    private static (WindowsKeyTable Table, WindowsKeyTracker Tracker) Make()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });
        return (table, new WindowsKeyTracker(table.Swallows));
    }

    private static bool Host(string id) => id.StartsWith("host.", StringComparison.Ordinal);

    [Theory]
    [InlineData(Left, "previous")]
    [InlineData(Right, "next")]
    public void S26_6_win_ctrl_arrows_switch_with_no_plugin_running(uint key, string direction)
    {
        var (table, tracker) = Make();
        table.Refresh(Host, Host);

        tracker.OnKey(WindowsKeyTracker.LeftWin, down: true);
        tracker.OnKey(Ctrl, down: true);
        var verdict = tracker.OnKey(key, down: true);

        Assert.True(verdict.Swallow);
        var chord = verdict.Chord!.Value;
        Assert.Equal(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, key), chord);
        var binding = table.Find(chord)!;
        Assert.Equal(DesktopCommands.Switch, binding.CommandId);
        Assert.Equal(direction, binding.Args!["direction"]!.GetValue<string>());
    }

    [Fact]
    public void S26_6_win_ctrl_d_creates_and_switches_and_win_ctrl_f4_removes_the_current_desktop()
    {
        var (table, _) = Make();
        table.Refresh(Host, Host);

        var create = table.Find(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, 'D'))!;
        var remove = table.Find(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, F4))!;

        Assert.Equal(DesktopCommands.Create, create.CommandId);
        Assert.True(create.Args!["switchTo"]!.GetValue<bool>());
        Assert.Equal(DesktopCommands.Remove, remove.CommandId);
        Assert.Equal(DesktopCommands.CurrentDesktop, remove.Args!["id"]!.GetValue<string>());
    }

    [Fact]
    public void S26_6_win_d_still_shows_the_desktop_and_is_not_confused_with_win_ctrl_d()
    {
        var (table, tracker) = Make();
        table.Refresh(Host, Host);

        tracker.OnKey(WindowsKeyTracker.LeftWin, down: true);
        var plain = tracker.OnKey('D', down: true);

        Assert.Equal("host.desktop.show", table.Find(plain.Chord!.Value)!.CommandId);
    }

    [Fact]
    public void S26_6_win_tab_passes_through_until_the_overview_plugin_is_running()
    {
        var (table, tracker) = Make();
        table.Refresh(Host, Host);

        tracker.OnKey(WindowsKeyTracker.LeftWin, down: true);
        var refused = tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        tracker.OnKey(WindowsKeyTracker.LeftWin, down: false);

        table.Refresh(id => Host(id) || id == WindowsKeyTable.OverviewCommand, _ => true);
        tracker.OnKey(WindowsKeyTracker.LeftWin, down: true);
        var taken = tracker.OnKey(Tab, down: true);

        Assert.False(refused.Swallow);
        Assert.Null(refused.Chord);
        Assert.True(taken.Swallow);
        Assert.Equal(WindowsKeyTable.OverviewCommand, table.Find(taken.Chord!.Value)!.CommandId);
    }

    [Fact]
    public void S26_6_an_unbound_win_ctrl_key_goes_to_windows()
    {
        var (table, tracker) = Make();
        table.Refresh(Host, Host);

        tracker.OnKey(WindowsKeyTracker.LeftWin, down: true);
        tracker.OnKey(Ctrl, down: true);
        var verdict = tracker.OnKey('X', down: true);

        Assert.False(verdict.Swallow);
        Assert.Null(verdict.Chord);
    }
}
