using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>What the keyboard hook does with one key event, and the chord it completed, if any.</summary>
/// <param name="Swallow">True to keep the event from Windows and every other program.</param>
/// <param name="Chord">A bound chord this event completed.</param>
/// <param name="Mask">
/// True when Windows is about to see the Windows key go up with nothing between: the hook sends a key of no
/// meaning first, so Windows does not open its own Start menu.
/// </param>
public readonly record struct KeyVerdict(bool Swallow, KeyChord? Chord, bool Mask);

/// <summary>
/// Follows the Windows key from the keyboard hook's events. A lone press and release completes
/// <c>Win</c> (virtual key 0), and Ctrl+Esc completes itself, swallowed. A bound Windows-key shortcut is
/// swallowed and completed; every other key goes to Windows, so Win+L still locks. Not thread-safe: the
/// hook's thread is its only caller.
/// </summary>
public sealed class WindowsKeyTracker
{
    public const uint LeftWin = 0x5B;
    public const uint RightWin = 0x5C;
    public const uint Escape = 0x1B;

    private readonly Func<KeyModifiers, uint, bool> _bound;
    private readonly HashSet<uint> _held = [];
    private readonly HashSet<uint> _swallowed = [];
    private bool _winOther;
    private bool _winPassedOther;

    /// <summary>A tracker that swallows Win+key for the keys <paramref name="boundWithWin"/> names.</summary>
    public WindowsKeyTracker(Func<uint, bool> boundWithWin) : this((modifiers, vk) => modifiers == KeyModifiers.Win && boundWithWin(vk))
    {
    }

    /// <summary>A tracker that swallows any chord that holds Windows and that <paramref name="bound"/> accepts (Win+Ctrl+Left, say).</summary>
    public WindowsKeyTracker(Func<KeyModifiers, uint, bool> bound)
    {
        _bound = bound;
    }

    /// <summary>A tracker whose Win-bound keys never change.</summary>
    public WindowsKeyTracker(IReadOnlySet<uint> boundWithWin) : this(boundWithWin.Contains)
    {
    }

    public KeyVerdict OnKey(uint vk, bool down)
    {
        if (!down && _swallowed.Remove(vk))
        {
            _held.Remove(vk);
            return new KeyVerdict(true, null, false);
        }

        if (vk is LeftWin or RightWin)
        {
            return down ? WinDown(vk) : WinUp(vk);
        }

        if (!down)
        {
            _held.Remove(vk);
            return default;
        }

        var repeat = !_held.Add(vk);
        if (_swallowed.Contains(vk))
        {
            return new KeyVerdict(true, null, false);
        }

        var modifiers = Modifiers();
        if (modifiers.HasFlag(KeyModifiers.Win))
        {
            _winOther = true;
            if (!repeat && _bound(modifiers, vk))
            {
                _swallowed.Add(vk);
                return new KeyVerdict(true, new KeyChord(modifiers, vk), false);
            }

            _winPassedOther = true;
            return default;
        }

        if (vk == Escape && modifiers == KeyModifiers.Ctrl)
        {
            _swallowed.Add(vk);
            return new KeyVerdict(true, repeat ? null : new KeyChord(KeyModifiers.Ctrl, Escape), false);
        }

        return default;
    }

    private KeyVerdict WinDown(uint vk)
    {
        if (!_held.Add(vk))
        {
            return default;
        }

        if (!(_held.Contains(LeftWin) && _held.Contains(RightWin)))
        {
            // A first Windows key down: the press starts.
            _winOther = _held.Count > 1;
            _winPassedOther = _winOther;
        }

        return default;
    }

    private KeyVerdict WinUp(uint vk)
    {
        if (!_held.Remove(vk))
        {
            return default;
        }

        if (_held.Contains(LeftWin) || _held.Contains(RightWin))
        {
            return default;
        }

        var lone = !_winOther;
        var mask = !_winPassedOther;
        _winOther = false;
        _winPassedOther = false;
        return new KeyVerdict(false, lone ? new KeyChord(KeyModifiers.Win, 0) : null, mask);
    }

    private KeyModifiers Modifiers()
    {
        var modifiers = KeyModifiers.None;
        foreach (var key in _held)
        {
            modifiers |= key switch
            {
                LeftWin or RightWin => KeyModifiers.Win,
                0x11 or 0xA2 or 0xA3 => KeyModifiers.Ctrl,
                0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift,
                0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt,
                _ => KeyModifiers.None,
            };
        }

        return modifiers;
    }
}

/// <summary>A Windows-key chord and the command it invokes, with <c>source: hotkey</c>.</summary>
public sealed record WindowsKeyBinding(KeyChord Chord, string CommandId, JsonObject? Args);

/// <summary>The Windows-key shortcuts the host keeps when it is the shell.</summary>
public static class WindowsKeyBindings
{
    public static IReadOnlyList<WindowsKeyBinding> Defaults { get; } =
    [
        new(new KeyChord(KeyModifiers.Win, 0), "launcher.open", null),
        new(new KeyChord(KeyModifiers.Ctrl, WindowsKeyTracker.Escape), "launcher.open", null),
        new(new KeyChord(KeyModifiers.Win, 'R'), "launcher.open", new JsonObject { ["mode"] = "run" }),
        new(new KeyChord(KeyModifiers.Win, 'E'), "host.files.open", null),
        new(new KeyChord(KeyModifiers.Win, 'D'), "host.desktop.show", null),
        new(new KeyChord(KeyModifiers.Win, 'M'), "host.desktop.minimizeAll", null),
    ];

    /// <summary>The keys a tracker swallows while Windows is held: those a binding names.</summary>
    public static IReadOnlySet<uint> KeysWithWin(IEnumerable<WindowsKeyBinding> bindings) =>
        bindings.Where(b => b.Chord.Modifiers == KeyModifiers.Win && b.Chord.VirtualKey != 0).Select(b => b.Chord.VirtualKey).ToHashSet();
}
