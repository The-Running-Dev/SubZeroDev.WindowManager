using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>S31.1 and S31.2: the Alt+Tab sequence, and Alt+Tab passing through while the switcher is not running.</summary>
public sealed class SwitcherChordTests
{
    private const uint LeftAlt = 0xA4;
    private const uint LeftShift = 0xA0;
    private const uint Tab = 0x09;
    private const uint Escape = 0x1B;

    private static (WindowsKeyTable Table, WindowsKeyTracker Tracker) Make(bool switcherRunning)
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });
        table.Refresh(
            id => id.StartsWith("host.", StringComparison.Ordinal) || (switcherRunning && id.StartsWith("switcher.", StringComparison.Ordinal)),
            _ => true);
        return (table, new WindowsKeyTracker(table.Swallows));
    }

    [Fact]
    public void S31_1_alt_tab_steps_on_each_tab_and_commits_when_alt_is_released()
    {
        var (table, tracker) = Make(switcherRunning: true);

        tracker.OnKey(LeftAlt, down: true);
        var first = tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var second = tracker.OnKey(Tab, down: true);
        var repeat = tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var release = tracker.OnKey(LeftAlt, down: false);

        Assert.True(first.Swallow);
        Assert.Equal(new KeyChord(KeyModifiers.Alt, Tab), first.Chord);
        Assert.Equal(new KeyChord(KeyModifiers.Alt, Tab), second.Chord);
        Assert.Equal(new KeyChord(KeyModifiers.Alt, Tab), repeat.Chord);
        Assert.Equal(WindowsKeyTable.SwitcherStepCommand, table.Find(first.Chord!.Value)!.CommandId);
        Assert.False(table.Find(first.Chord!.Value)!.Args!["backwards"]!.GetValue<bool>());
        Assert.Equal(new KeyChord(KeyModifiers.Alt, 0), release.Chord);
        Assert.Equal(WindowsKeyTable.SwitcherCommitCommand, table.Find(release.Chord!.Value)!.CommandId);
    }

    [Fact]
    public void S31_1_the_alt_release_is_masked_so_the_program_in_front_opens_no_menu_bar()
    {
        var (_, tracker) = Make(switcherRunning: true);

        tracker.OnKey(LeftAlt, down: true);
        tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var release = tracker.OnKey(LeftAlt, down: false);

        Assert.True(release.Mask);
        Assert.False(release.Swallow);
    }

    [Fact]
    public void S31_1_shift_steps_backwards()
    {
        var (table, tracker) = Make(switcherRunning: true);

        tracker.OnKey(LeftAlt, down: true);
        tracker.OnKey(LeftShift, down: true);
        var verdict = tracker.OnKey(Tab, down: true);

        Assert.True(verdict.Swallow);
        var binding = table.Find(verdict.Chord!.Value)!;
        Assert.Equal(WindowsKeyTable.SwitcherStepCommand, binding.CommandId);
        Assert.True(binding.Args!["backwards"]!.GetValue<bool>());
    }

    [Fact]
    public void S31_1_escape_cancels_and_the_release_that_follows_commits_nothing()
    {
        var (table, tracker) = Make(switcherRunning: true);

        tracker.OnKey(LeftAlt, down: true);
        tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var cancel = tracker.OnKey(Escape, down: true);
        tracker.OnKey(Escape, down: false);
        var lateTab = tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var release = tracker.OnKey(LeftAlt, down: false);

        Assert.True(cancel.Swallow);
        Assert.Equal(WindowsKeyTable.SwitcherCancelCommand, table.Find(cancel.Chord!.Value)!.CommandId);
        Assert.True(lateTab.Swallow);
        Assert.Null(lateTab.Chord);
        Assert.Null(release.Chord);
        Assert.True(release.Mask);
    }

    [Fact]
    public void S31_1_a_new_sequence_starts_cleanly_after_a_commit()
    {
        var (_, tracker) = Make(switcherRunning: true);

        tracker.OnKey(LeftAlt, down: true);
        tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        tracker.OnKey(LeftAlt, down: false);

        tracker.OnKey(LeftAlt, down: true);
        var again = tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var release = tracker.OnKey(LeftAlt, down: false);

        Assert.Equal(new KeyChord(KeyModifiers.Alt, Tab), again.Chord);
        Assert.Equal(new KeyChord(KeyModifiers.Alt, 0), release.Chord);
    }

    [Fact]
    public void S31_1_alt_alone_and_alt_with_other_keys_are_left_to_windows()
    {
        var (_, tracker) = Make(switcherRunning: true);

        var down = tracker.OnKey(LeftAlt, down: true);
        var f = tracker.OnKey('F', down: true);
        tracker.OnKey('F', down: false);
        var escape = tracker.OnKey(Escape, down: true);
        tracker.OnKey(Escape, down: false);
        var up = tracker.OnKey(LeftAlt, down: false);

        Assert.False(down.Swallow);
        Assert.False(f.Swallow);
        Assert.False(escape.Swallow);
        Assert.Null(escape.Chord);
        Assert.False(up.Swallow);
        Assert.False(up.Mask);
        Assert.Null(up.Chord);
    }

    [Fact]
    public void S31_2_alt_tab_goes_to_windows_while_the_switcher_is_not_running()
    {
        var (_, tracker) = Make(switcherRunning: false);

        tracker.OnKey(LeftAlt, down: true);
        var tab = tracker.OnKey(Tab, down: true);
        tracker.OnKey(Tab, down: false);
        var release = tracker.OnKey(LeftAlt, down: false);

        Assert.False(tab.Swallow);
        Assert.Null(tab.Chord);
        Assert.False(release.Swallow);
        Assert.False(release.Mask);
        Assert.Null(release.Chord);
    }

    [Fact]
    public void S31_2_alt_tab_binds_only_once_its_commands_exist()
    {
        var (table, _) = Make(switcherRunning: true);
        var (absent, _) = Make(switcherRunning: false);

        Assert.True(table.Swallows(KeyModifiers.Alt, Tab));
        Assert.True(table.Swallows(KeyModifiers.Alt | KeyModifiers.Shift, Tab));
        Assert.False(absent.Swallows(KeyModifiers.Alt, Tab));
        Assert.False(absent.Swallows(KeyModifiers.Alt | KeyModifiers.Shift, Tab));
    }

    [Fact]
    public void S31_1_win_chords_are_unaffected_by_the_alt_sequence()
    {
        var (table, tracker) = Make(switcherRunning: true);

        tracker.OnKey(WindowsKeyTracker.LeftWin, down: true);
        var verdict = tracker.OnKey('D', down: true);

        Assert.Equal("host.desktop.show", table.Find(verdict.Chord!.Value)!.CommandId);
    }
}
