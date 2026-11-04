using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The Windows-key chord table (second release): the first release's fixed bindings, Win+N for the
/// notification centre, and the user's <c>windowsKeys</c> from <c>settings.json</c> over both. A first-release
/// binding is always swallowed, as before. Every other chord, and a user's replacement of a first-release one,
/// is swallowed only while its command has a provider that is running (I35), so a key never disappears into a
/// plugin that cannot answer; a replaced first-release chord falls back to its own binding meanwhile. The hook's
/// thread reads a snapshot; it never calls into the UI thread or waits.
/// </summary>
public sealed partial class WindowsKeyTable
{
    public const string NotificationCentreCommand = "notifications.openCentre";

    /// <summary>The panel command of the <c>subzerodev.quick-settings</c> plugin; Win+A is left to Windows while it is not running (S27.5).</summary>
    public const string QuickSettingsCommand = "quicksettings.open";

    /// <summary>The menu command of the <c>subzerodev.power-menu</c> plugin; Win+X is left to Windows while it is not running (S28.2).</summary>
    public const string PowerMenuCommand = "powermenu.open";

    /// <summary>The overview command of the <c>subzerodev.desktops</c> plugin; Win+Tab is left to Windows while it is not running (S26.6).</summary>
    public const string OverviewCommand = "desktops.openOverview";

    private const uint Tab = 0x09;
    private const uint Left = 0x25;
    private const uint Right = 0x27;
    private const uint F4 = 0x73;

    /// <summary>The virtual-desktop chords (S26): the host's own commands, so they work whatever plugins run; only Win+Tab needs the plugin.</summary>
    public static IReadOnlyList<WindowsKeyBinding> DesktopBindings { get; } =
    [
        new(new KeyChord(KeyModifiers.Win, Tab), OverviewCommand, null),
        new(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, Left), DesktopCommands.Switch, new JsonObject { ["direction"] = "previous" }),
        new(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, Right), DesktopCommands.Switch, new JsonObject { ["direction"] = "next" }),
        new(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, 'D'), DesktopCommands.Create, new JsonObject { ["switchTo"] = true }),
        new(new KeyChord(KeyModifiers.Win | KeyModifiers.Ctrl, F4), DesktopCommands.Remove, new JsonObject { ["id"] = DesktopCommands.CurrentDesktop }),
    ];

    /// <summary>One chord: what the table or user binds it to, and the first-release binding it falls back to.</summary>
    private sealed record Entry(WindowsKeyBinding Binding, WindowsKeyBinding? Fallback);

    private readonly Dictionary<KeyChord, Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly HashSet<string> _reported = [];
    private volatile Snapshot _snapshot = new(new Dictionary<KeyChord, WindowsKeyBinding>(), new HashSet<uint>());

    /// <param name="settings">Chord text → command id, from <c>settings.json</c>; an unknown chord is ignored with a diagnostic.</param>
    /// <param name="warn">Receives a diagnostic for each chord that cannot be used.</param>
    public WindowsKeyTable(IReadOnlyDictionary<string, string> settings, Action<string> warn)
    {
        foreach (var binding in WindowsKeyBindings.Defaults)
        {
            _entries[binding.Chord] = new Entry(binding, null);
        }

        var centre = new KeyChord(KeyModifiers.Win, 'N');
        _entries[centre] = new Entry(new WindowsKeyBinding(centre, NotificationCentreCommand, null), null);
        var quick = new KeyChord(KeyModifiers.Win, 'A');
        _entries[quick] = new Entry(new WindowsKeyBinding(quick, QuickSettingsCommand, null), null);
        var powerMenu = new KeyChord(KeyModifiers.Win, 'X');
        _entries[powerMenu] = new Entry(new WindowsKeyBinding(powerMenu, PowerMenuCommand, null), null);
        foreach (var binding in DesktopBindings)
        {
            _entries[binding.Chord] = new Entry(binding, null);
        }

        foreach (var (text, commandId) in settings)
        {
            var parsed = KeyChord.Parse(text);
            if (!IsChord(text) || !parsed.Ok)
            {
                warn($"windowsKeys: '{text}' is not a key chord; ignored.");
                continue;
            }

            var fallback = _entries.TryGetValue(parsed.Value, out var existing) ? existing.Fallback ?? Default(existing) : null;
            _entries[parsed.Value] = new Entry(new WindowsKeyBinding(parsed.Value, commandId, null), fallback);
        }

        Refresh(_ => false, _ => false);
    }

    /// <summary>Matches the contract's chord grammar: modifiers joined with <c>+</c>, then one letter or digit.</summary>
    public static bool IsChord(string? text) => text is not null && ChordPattern().IsMatch(text);

    /// <summary>The bindings, one per chord, as configured (before any provider is considered).</summary>
    public IReadOnlyList<WindowsKeyBinding> Bindings => [.. _entries.Values.Select(e => e.Binding)];

    /// <summary>The binding in force for a chord the hook completed; null when none is.</summary>
    public WindowsKeyBinding? Find(KeyChord chord) => _snapshot.Effective.GetValueOrDefault(chord);

    /// <summary>True when the hook should swallow <c>Win+vk</c>: bound, and its command able to answer.</summary>
    public bool SwallowsWithWin(uint vk) => _snapshot.WithWin.Contains(vk);

    /// <summary>True when the hook should swallow this chord (it holds Windows): bound, and its command able to answer.</summary>
    public bool Swallows(KeyModifiers modifiers, uint vk) =>
        modifiers.HasFlag(KeyModifiers.Win) && vk != 0 && _snapshot.Effective.ContainsKey(new KeyChord(modifiers, vk));

    /// <summary>Rebuilds the snapshot after a command's provider starts or stops.</summary>
    /// <param name="exists">Whether any plugin or the host declares the command.</param>
    /// <param name="available">Whether the command's provider is running (a built-in always is).</param>
    public void Refresh(Func<string, bool> exists, Func<string, bool> available)
    {
        // Provider changes arrive from several threads; the last one to publish must also be the last one to build.
        lock (_gate)
        {
            var effective = new Dictionary<KeyChord, WindowsKeyBinding>();
            var keys = new HashSet<uint>();
            foreach (var (chord, entry) in _entries)
            {
                var binding = IsDefault(entry) || (exists(entry.Binding.CommandId) && available(entry.Binding.CommandId))
                    ? entry.Binding
                    : entry.Fallback;
                if (binding is null)
                {
                    continue;
                }

                effective[chord] = binding;
                if (chord.Modifiers == KeyModifiers.Win && chord.VirtualKey != 0)
                {
                    keys.Add(chord.VirtualKey);
                }
            }

            _snapshot = new Snapshot(effective, keys);
        }
    }

    /// <summary>The user's and Win+N's command ids that nothing declares, for a diagnostic.</summary>
    public IReadOnlyList<string> Unresolved(Func<string, bool> exists) =>
        [.. _entries.Values.Where(e => !IsDefault(e) && !exists(e.Binding.CommandId)).Select(e => $"{e.Binding.Chord} → {e.Binding.CommandId}")];

    /// <summary>Warns once for each binding whose command nothing declares; a binding that later resolves may warn again.</summary>
    /// <param name="exists">Whether any plugin or the host declares the command.</param>
    /// <param name="warn">Receives the diagnostic.</param>
    public void ReportUnresolved(Func<string, bool> exists, Action<string> warn)
    {
        lock (_gate)
        {
            var current = Unresolved(exists);
            _reported.IntersectWith(current);
            foreach (var line in current.Where(_reported.Add))
            {
                warn($"windowsKeys: {line} names a command nothing declares; the chord is left to Windows.");
            }
        }
    }

    private static WindowsKeyBinding? Default(Entry entry) => IsDefault(entry) ? entry.Binding : null;

    /// <summary>True for an entry that is a first-release binding: no provider is needed for it.</summary>
    private static bool IsDefault(Entry entry) => WindowsKeyBindings.Defaults.Contains(entry.Binding);

    [GeneratedRegex(@"^(Win|Ctrl|Alt|Shift)(\+(Win|Ctrl|Alt|Shift))*\+[A-Za-z0-9]+$")]
    private static partial Regex ChordPattern();

    private sealed record Snapshot(IReadOnlyDictionary<KeyChord, WindowsKeyBinding> Effective, IReadOnlySet<uint> WithWin);
}
