using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>One command's hotkey: <c>bound</c>, <c>in-use</c> (another program holds it) or <c>invalid</c>.</summary>
public sealed record HotkeyBinding(string CommandId, string Chord, string State);

/// <summary>
/// Registers a global hotkey for each command: the manifest's <c>defaultHotkey</c>, unless
/// <c>hotkeys</c> in host settings overrides it (null unbinds). A chord another program holds stays
/// unbound for the session.
/// </summary>
public sealed class HotkeyBinder : IDisposable
{
    private readonly ICommandRouter _router;
    private readonly IHotkeyService _hotkeys;
    private readonly IConfigStore _config;
    private readonly IHostLog _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (KeyChord Chord, HotkeyRegistration Registration)> _bound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HotkeyBinding> _failed = new(StringComparer.Ordinal);
    private bool _disposed;

    public HotkeyBinder(ICommandRouter router, IHotkeyService hotkeys, IConfigStore config, IHostLog log)
    {
        _router = router;
        _hotkeys = hotkeys;
        _config = config;
        _log = log;
        _router.CommandsChanged += (_, _) => Refresh();
        _config.Changed += (_, e) =>
        {
            if (e.Id == DocumentId.HostSettings)
            {
                Refresh();
            }
        };
    }

    public IReadOnlyList<HotkeyBinding> Bindings
    {
        get
        {
            lock (_gate)
            {
                return [.. _bound.Select(b => new HotkeyBinding(b.Key, b.Value.Chord.ToString(), "bound"))
                    .Concat(_failed.Values)
                    .OrderBy(b => b.CommandId, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Brings registrations in line with the commands and settings. Safe to call repeatedly.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var wanted = Wanted();
            foreach (var commandId in _bound.Keys.ToList())
            {
                if (!wanted.TryGetValue(commandId, out var chord) || chord != _bound[commandId].Chord)
                {
                    _bound[commandId].Registration.Dispose();
                    _bound.Remove(commandId);
                }
            }

            foreach (var (commandId, chord) in wanted)
            {
                if (_bound.ContainsKey(commandId)
                    || (_failed.TryGetValue(commandId, out var failed) && failed.Chord == chord.ToString()))
                {
                    continue;
                }

                var id = commandId;
                var registration = _hotkeys.Register(chord, () => _ = InvokeAsync(id));
                if (registration.Ok)
                {
                    _failed.Remove(commandId);
                    _bound[commandId] = (chord, registration.Value!);
                    _log.Write(LogLevel.Info, "hotkeys", $"{chord} invokes {commandId}.");
                }
                else
                {
                    _failed[commandId] = new HotkeyBinding(commandId, chord.ToString(), "in-use");
                    _log.Write(LogLevel.Warning, "hotkeys", $"{commandId} is unbound: {registration.Error!.Detail}");
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var (_, (_, registration)) in _bound)
            {
                registration.Dispose();
            }

            _bound.Clear();
        }
    }

    private Dictionary<string, KeyChord> Wanted()
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (_config.Get(DocumentId.HostSettings) is { Health: DocumentHealth.Valid, Content: { } content }
            && content["hotkeys"] is JsonObject map)
        {
            foreach (var (commandId, value) in map)
            {
                overrides[commandId] = value is JsonValue v && v.TryGetValue<string>(out var chord) ? chord : null;
            }
        }

        var wanted = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
        foreach (var command in _router.Commands)
        {
            var text = overrides.TryGetValue(command.CommandId, out var chosen) ? chosen : command.DefaultHotkey;
            if (text is null)
            {
                continue;
            }

            var parsed = KeyChord.Parse(text);
            if (parsed.Ok)
            {
                wanted[command.CommandId] = parsed.Value;
            }
            else
            {
                _failed[command.CommandId] = new HotkeyBinding(command.CommandId, text, "invalid");
            }
        }

        return wanted;
    }

    private async Task InvokeAsync(string commandId)
    {
        var result = await _router.InvokeAsync(commandId, null, InvocationSource.Hotkey, null, CancellationToken.None).ConfigureAwait(false);
        if (!result.Ok)
        {
            _log.Write(LogLevel.Warning, "hotkeys", $"{commandId}: {result.Error!.Code} {result.Error.Detail}");
        }
    }
}
