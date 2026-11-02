using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Recovery;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>
/// Slice S21: the Windows-key tracker the keyboard hook asks about each event, the default bindings, and the
/// recording session service. No hook is installed and no key reaches Windows.
/// </summary>
public sealed class WindowsKeyTests : IDisposable
{
    private const uint LeftWin = WindowsKeyTracker.LeftWin;
    private const uint LeftCtrl = 0xA2;
    private const uint LeftShift = 0xA0;

    private readonly Scratch _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static WindowsKeyTracker Tracker() =>
        new(WindowsKeyBindings.KeysWithWin(WindowsKeyBindings.Defaults));

    [Fact]
    public void S21_1_a_lone_windows_key_completes_win_and_masks_the_release()
    {
        var tracker = Tracker();

        Assert.Equal(default, tracker.OnKey(LeftWin, down: true));
        var up = tracker.OnKey(LeftWin, down: false);

        Assert.False(up.Swallow);
        Assert.Equal(new KeyChord(KeyModifiers.Win, 0), up.Chord);
        Assert.True(up.Mask);
    }

    [Fact]
    public void S21_1_ctrl_esc_completes_itself_and_both_halves_are_swallowed()
    {
        var tracker = Tracker();

        tracker.OnKey(LeftCtrl, down: true);
        var down = tracker.OnKey(WindowsKeyTracker.Escape, down: true);
        var repeat = tracker.OnKey(WindowsKeyTracker.Escape, down: true);
        var up = tracker.OnKey(WindowsKeyTracker.Escape, down: false);
        var ctrlUp = tracker.OnKey(LeftCtrl, down: false);

        Assert.True(down.Swallow);
        Assert.Equal(new KeyChord(KeyModifiers.Ctrl, WindowsKeyTracker.Escape), down.Chord);
        Assert.True(repeat.Swallow);
        Assert.Null(repeat.Chord);
        Assert.True(up.Swallow);
        Assert.False(ctrlUp.Swallow);
    }

    [Fact]
    public void S21_1_windows_key_with_an_unbound_key_passes_and_completes_nothing()
    {
        var tracker = Tracker();

        tracker.OnKey(LeftWin, down: true);
        var x = tracker.OnKey('X', down: true);
        var xUp = tracker.OnKey('X', down: false);
        var winUp = tracker.OnKey(LeftWin, down: false);

        Assert.Equal(default, x);
        Assert.Equal(default, xUp);
        Assert.Null(winUp.Chord);
        Assert.False(winUp.Mask);
        Assert.False(winUp.Swallow);
    }

    [Fact]
    public void S21_2_a_bound_shortcut_is_swallowed_once_and_the_release_is_masked()
    {
        var tracker = Tracker();

        tracker.OnKey(LeftWin, down: true);
        var r = tracker.OnKey('R', down: true);
        var repeat = tracker.OnKey('R', down: true);
        var rUp = tracker.OnKey('R', down: false);
        var winUp = tracker.OnKey(LeftWin, down: false);

        Assert.True(r.Swallow);
        Assert.Equal(new KeyChord(KeyModifiers.Win, 'R'), r.Chord);
        Assert.True(repeat.Swallow);
        Assert.Null(repeat.Chord);
        Assert.True(rUp.Swallow);
        // Windows saw only the Windows key go down and up, so it would open Start without the mask.
        Assert.Null(winUp.Chord);
        Assert.True(winUp.Mask);
    }

    [Fact]
    public void S21_2_win_l_and_a_shortcut_with_another_modifier_go_to_windows()
    {
        var tracker = Tracker();

        tracker.OnKey(LeftWin, down: true);
        Assert.Equal(default, tracker.OnKey('L', down: true));
        tracker.OnKey('L', down: false);
        tracker.OnKey(LeftShift, down: true);
        Assert.Equal(default, tracker.OnKey('R', down: true));
        tracker.OnKey('R', down: false);
        tracker.OnKey(LeftShift, down: false);
        var winUp = tracker.OnKey(LeftWin, down: false);

        Assert.Null(winUp.Chord);
        Assert.False(winUp.Mask);
    }

    [Fact]
    public void S21_2_a_key_held_before_the_windows_key_makes_the_press_not_lone()
    {
        var tracker = Tracker();

        tracker.OnKey('A', down: true);
        tracker.OnKey(LeftWin, down: true);
        tracker.OnKey('A', down: false);
        var winUp = tracker.OnKey(LeftWin, down: false);

        Assert.Null(winUp.Chord);
    }

    [Fact]
    public void S21_2_the_default_bindings_name_the_commands_in_the_slice()
    {
        var byChord = WindowsKeyBindings.Defaults.ToDictionary(b => b.Chord);

        Assert.Equal("launcher.open", byChord[new KeyChord(KeyModifiers.Win, 0)].CommandId);
        Assert.Equal("launcher.open", byChord[new KeyChord(KeyModifiers.Ctrl, WindowsKeyTracker.Escape)].CommandId);
        var run = byChord[new KeyChord(KeyModifiers.Win, 'R')];
        Assert.Equal("launcher.open", run.CommandId);
        Assert.Equal("run", (string?)run.Args?["mode"]);
        Assert.Equal("host.files.open", byChord[new KeyChord(KeyModifiers.Win, 'E')].CommandId);
        Assert.Equal("host.desktop.show", byChord[new KeyChord(KeyModifiers.Win, 'D')].CommandId);
        Assert.Equal("host.desktop.minimizeAll", byChord[new KeyChord(KeyModifiers.Win, 'M')].CommandId);
        Assert.Equal(new HashSet<uint> { 'R', 'E', 'D', 'M' }, WindowsKeyBindings.KeysWithWin(WindowsKeyBindings.Defaults));
    }

    [Fact]
    public void S21_4_the_recording_session_service_records_and_refuses_as_told()
    {
        var hook = _scratch.DataRoot;
        File.WriteAllText(Path.Combine(hook, RecordingSessionService.RefuseFile), "restart\n");
        var session = new RecordingSessionService(hook);

        Assert.True(session.Lock().Ok);
        Assert.True(session.Sleep().Ok);
        var refused = session.Restart();

        Assert.Equal(CoreErrorCode.SessionCommandRefused, refused.Error?.Code);
        Assert.Equal(["session-lock", "session-sleep", "session-restart refused"],
            File.ReadAllLines(Path.Combine(hook, Reverter.DesktopActionsLog)));
    }
}
