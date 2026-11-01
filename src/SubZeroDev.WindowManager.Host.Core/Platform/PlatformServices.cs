using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

public sealed record MonitorInfo(string StableId, nint Handle, RectInt Bounds, RectInt WorkArea, uint Dpi, bool IsPrimary)
{
    public double Scale => Dpi / 96.0;
}

public readonly record struct RectInt(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public override string ToString() => $"{Left},{Top},{Right},{Bottom}";
}

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> Monitors { get; }

    event EventHandler TopologyChanged;   // debounced 250 ms
}

public interface IAppBarService
{
    Task<Outcome<RectInt>> RegisterAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct); // journals first

    Task<Outcome<RectInt>> UpdateAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct);

    Task<Outcome> RemoveAsync(nint hwnd, CancellationToken ct);   // reverts, then ends the journal entry

    /// <summary>
    /// Removes the app bar on the calling thread, for close paths that cannot wait for a task: a
    /// window that is closing, or the process exiting. Does nothing when the window holds none.
    /// </summary>
    void RemoveNow(nint hwnd);

    event EventHandler<FullscreenChangedEventArgs> FullscreenChanged;

    event EventHandler ShellRestarted;    // "TaskbarCreated"
}

public sealed record FullscreenChangedEventArgs(string MonitorId, bool IsFullscreen);

/// <summary>Write-ahead record of desktop changes, implemented over the Recovery module.</summary>
public interface IRecoveryJournal
{
    Task<Outcome<Guid>> BeginAsync(Recovery.DesktopChange change, CancellationToken ct);   // durable before return (I6)

    Task<Outcome> EndAsync(Guid changeId, CancellationToken ct);                  // after revert

    Task<Outcome> MarkCleanExitAsync(CancellationToken ct);
}

/// <summary>The modifier flags match <c>RegisterHotKey</c>'s.</summary>
[Flags]
public enum KeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>A hotkey such as <c>Ctrl+Alt+Space</c>: modifiers and one virtual key.</summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> Keys = BuildKeys();

    /// <summary>
    /// Parses <c>Modifier+…+Key</c>, case-insensitively. Modifiers are Ctrl (or Control), Alt, Shift and Win.
    /// At least one modifier is needed, except for the function keys.
    /// </summary>
    public static Outcome<KeyChord> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Outcome<KeyChord>.Fail(CoreErrorCode.NotFound, "A key chord is empty.");
        }

        var modifiers = KeyModifiers.None;
        uint? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            KeyModifiers? modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Ctrl,
                "ALT" => KeyModifiers.Alt,
                "SHIFT" => KeyModifiers.Shift,
                "WIN" => KeyModifiers.Win,
                _ => null,
            };
            if (modifier is { } m)
            {
                if (modifiers.HasFlag(m) || key is not null)
                {
                    return Outcome<KeyChord>.Fail(CoreErrorCode.NotFound, $"'{text}' repeats a modifier or puts one after the key.");
                }

                modifiers |= m;
            }
            else if (key is null && Keys.TryGetValue(part.ToUpperInvariant(), out var vk))
            {
                key = vk;
            }
            else
            {
                return Outcome<KeyChord>.Fail(CoreErrorCode.NotFound, $"'{part}' in '{text}' is not a known key.");
            }
        }

        if (key is not { } virtualKey)
        {
            return Outcome<KeyChord>.Fail(CoreErrorCode.NotFound, $"'{text}' names no key.");
        }

        if (modifiers == KeyModifiers.None && virtualKey is < 0x70 or > 0x87)
        {
            return Outcome<KeyChord>.Fail(CoreErrorCode.NotFound, $"'{text}' needs a modifier.");
        }

        return Outcome<KeyChord>.Success(new KeyChord(modifiers, virtualKey));
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        var vk = VirtualKey;
        parts.Add(Names.FirstOrDefault(k => k.Key == vk).Value ?? $"0x{vk:X2}");
        return string.Join('+', parts);
    }

    private static readonly List<KeyValuePair<uint, string>> Names = BuildNames();

    private static Dictionary<string, uint> BuildKeys()
    {
        var keys = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (vk, name) in BuildNames())
        {
            keys.TryAdd(name.ToUpperInvariant(), vk);
        }

        keys["ESC"] = 0x1B;
        keys["DEL"] = 0x2E;
        keys["INS"] = 0x2D;
        return keys;
    }

    private static List<KeyValuePair<uint, string>> BuildNames()
    {
        var names = new List<KeyValuePair<uint, string>>();
        for (var c = 'A'; c <= 'Z'; c++) names.Add(KeyValuePair.Create((uint)c, c.ToString()));
        for (var d = '0'; d <= '9'; d++) names.Add(KeyValuePair.Create((uint)d, d.ToString()));
        for (var f = 1; f <= 24; f++) names.Add(KeyValuePair.Create((uint)(0x6F + f), "F" + f.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        for (var n = 0; n <= 9; n++) names.Add(KeyValuePair.Create((uint)(0x60 + n), "NumPad" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var (vk, name) in new (uint, string)[]
        {
            (0x20, "Space"), (0x0D, "Enter"), (0x09, "Tab"), (0x1B, "Escape"), (0x08, "Backspace"), (0x2E, "Delete"),
            (0x2D, "Insert"), (0x24, "Home"), (0x23, "End"), (0x21, "PageUp"), (0x22, "PageDown"), (0x25, "Left"),
            (0x26, "Up"), (0x27, "Right"), (0x28, "Down"), (0x2C, "PrintScreen"), (0x13, "Pause"), (0xBB, "Plus"),
            (0xBD, "Minus"), (0xBC, "Comma"), (0xBE, "Period"), (0xBA, "Semicolon"), (0xBF, "Slash"),
            (0xDC, "Backslash"), (0xC0, "Backtick"), (0xDE, "Quote"), (0xDB, "LeftBracket"), (0xDD, "RightBracket"),
        })
        {
            names.Add(KeyValuePair.Create(vk, name));
        }

        return names;
    }
}

/// <summary>A registered hotkey; disposing it unregisters it.</summary>
public sealed class HotkeyRegistration(KeyChord chord, Action unregister) : IDisposable
{
    private Action? _unregister = unregister;

    public KeyChord Chord { get; } = chord;

    public void Dispose() => Interlocked.Exchange(ref _unregister, null)?.Invoke();
}

public interface IHotkeyService
{
    /// <summary>Fails with <see cref="CoreErrorCode.HotkeyInUse"/> when another process holds the chord. <paramref name="onPressed"/> runs off the UI thread.</summary>
    Outcome<HotkeyRegistration> Register(KeyChord chord, Action onPressed);
}

public interface IForegroundService
{
    /// <summary>Lets the process take the foreground once (<c>AllowSetForegroundWindow</c>, I18).</summary>
    void AllowForeground(int processId);

    /// <summary>Restores the window if it is minimised and brings it to the foreground. WindowGone or ForegroundDenied otherwise.</summary>
    Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct);

    Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct);
}
