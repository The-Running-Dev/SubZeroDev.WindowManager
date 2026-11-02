using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

[assembly: SupportedOSPlatform("windows")]

namespace SubZeroDev.WindowManager.Plugins.Indicators;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<IndicatorsPlugin>(args);
}

/// <summary>
/// The volume, network and battery items Explorer's notification area showed. Each is a panel button opening
/// its popup: volume has a mute toggle and a level slider, network and battery a line of status and a link to
/// their Settings page. Readings are taken four times a second and pushed only when they change; the battery
/// item hides itself on a machine without one.
/// </summary>
public sealed class IndicatorsPlugin : Plugin
{
    /// <summary>The test hook directory; with it set, readings come from a file and Settings is recorded, not opened.</summary>
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";

    private static readonly TimeSpan ReadEvery = TimeSpan.FromMilliseconds(250);

    private static readonly IReadOnlyDictionary<string, string> SettingsPages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["volume"] = "ms-settings:sound",
        ["network"] = "ms-settings:network",
        ["battery"] = "ms-settings:batterysaver",
    };

    private readonly string? _hook = Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook ? hook : null;
    private readonly Lock _gate = new();
    private IIndicatorSource _source = null!;
    private Readings? _readings;
    private Timer? _timer;
    private int _reading;

    internal event EventHandler? Changed;

    internal Readings? Current
    {
        get
        {
            lock (_gate)
            {
                return _readings;
            }
        }
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _source = _hook is null ? new SystemSource() : new FakeSource(_hook);
        Context.Popups.Get("volume").SetView(View.Stack("volume", StackOrientation.Vertical,
            View.Text("title", "Volume", "primary"),
            View.Stack("controls", StackOrientation.Horizontal,
                View.Toggle("mute", "Mute", Prop<bool>.State("muted"), View.Event("mute"), "Mute"),
                View.Slider("level", "Volume", Prop<double>.State("level"), View.Event("level"), step: 0.02),
                View.Text("percent", Prop<string>.State("percent"))),
            View.Text("status", Prop<string>.State("status"), "compact"),
            View.Button("settings", "Sound settings", View.Event("settings"), "Sound settings")));
        foreach (var popup in new[] { "network", "battery" })
        {
            Context.Popups.Get(popup).SetView(View.Stack(popup, StackOrientation.Vertical,
                View.Text("status", Prop<string>.State("status")),
                View.Button("settings", popup == "network" ? "Network settings" : "Battery settings", View.Event("settings"),
                    popup == "network" ? "Network settings" : "Battery settings")));
        }

        Refresh();
        _timer = new Timer(_ => Refresh(), null, ReadEvery, ReadEvery);
        return Task.CompletedTask;
    }

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        _source?.Dispose();
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new IndicatorInstance(request, this);

    protected override async Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        switch (e.ElementId)
        {
            case "level" when e.Kind == ElementEventKind.ValueChanged && e.Value is JsonValue value && value.TryGetValue<double>(out var level):
                _source.SetVolume(level);
                // Moving the level unmutes, as Windows' own slider does.
                if (Current?.Volume.Muted == true)
                {
                    _source.SetMute(false);
                }

                Refresh();
                break;
            case "mute" when e.Kind == ElementEventKind.Toggled && e.Value is JsonValue value && value.TryGetValue<bool>(out var muted):
                _source.SetMute(muted);
                Refresh();
                break;
            case "settings" when e.Kind == ElementEventKind.Invoked && SettingsPages.TryGetValue(popup.PopupId, out var page):
                OpenSettings(page);
                await popup.CloseAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    private void OpenSettings(string uri)
    {
        if (_hook is not null)
        {
            Directory.CreateDirectory(_hook);
            File.AppendAllText(Path.Combine(_hook, "desktop-actions.log"), $"settings-open {uri}\n", new UTF8Encoding(false));
            return;
        }

        AllowSetForegroundWindow(AsfwAny);
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    /// <summary>Reads the source; on a change, updates the popups and tells the panel items.</summary>
    private void Refresh()
    {
        // A slow read (a device waking) must not stack timer callbacks.
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            return;
        }

        try
        {
            var readings = _source.Read();
            lock (_gate)
            {
                if (readings == _readings)
                {
                    return;
                }

                _readings = readings;
            }

            var volume = Context.Popups.Get("volume");
            volume.SetState("muted", readings.Volume.Muted);
            volume.SetState("level", readings.Volume.Level);
            volume.SetState("percent", Describe.Percent(readings.Volume.Level));
            volume.SetState("status", readings.Volume.Present ? string.Empty : "No audio output device.");
            Context.Popups.Get("network").SetState("status", Describe.Network(readings.Network));
            Context.Popups.Get("battery").SetState("status", Describe.Battery(readings.Battery));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Context.Log.LogWarning("Reading the indicators failed: {Message}", e.Message);
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>The words shown for a reading, in the item's label and tooltip and in the popups.</summary>
internal static class Describe
{
    public static string Percent(double level) => $"{Math.Round(level * 100)}%";

    public static string Network(NetworkReading network) => network switch
    {
        NetworkReading.Connected => "Connected to the internet",
        NetworkReading.Limited => "Connected, no internet",
        _ => "Not connected",
    };

    public static string Battery(BatteryReading battery) => !battery.Present ? "No battery"
        : battery.Charging ? $"{battery.Percent}%, charging"
        : $"{battery.Percent}% remaining";
}

/// <summary>One indicator in a panel: a button showing its reading and opening its popup.</summary>
public sealed class IndicatorInstance(InstanceRequest request, IndicatorsPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        var name = ContributionId switch { "volume" => "Volume", "network" => "Network", _ => "Battery" };
        Surface.SetView(View.Button(ContributionId, name, View.Popup(ContributionId),
                Prop<string>.State("label"), Prop<IconRef>.State("icon"), null, "indicator")
            .WithTooltip(Prop<string>.State("tooltip"))
            .WithVisible(Prop<bool>.State("visible")));
        plugin.Changed += OnChanged;
        Show();
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        plugin.Changed -= OnChanged;
        return base.DisposeAsync();
    }

    private void OnChanged(object? sender, EventArgs e) => Show();

    private void Show()
    {
        if (plugin.Current is not { } readings)
        {
            return;
        }

        var (label, icon, tooltip, visible) = ContributionId switch
        {
            "volume" => readings.Volume switch
            {
                { Present: false } => ("No audio", "volume.none", "No audio output device", true),
                { Muted: true } => ("Muted", "volume.muted", $"Volume {Describe.Percent(readings.Volume.Level)}, muted", true),
                var volume => (Describe.Percent(volume.Level), "volume", $"Volume {Describe.Percent(volume.Level)}", true),
            },
            "network" => readings.Network switch
            {
                NetworkReading.Connected => ("Online", "network", Describe.Network(readings.Network), true),
                NetworkReading.Limited => ("Limited", "network.limited", Describe.Network(readings.Network), true),
                _ => ("Offline", "network.none", Describe.Network(readings.Network), true),
            },
            _ => ($"{readings.Battery.Percent}%", readings.Battery.Charging ? "battery.charging" : "battery",
                Describe.Battery(readings.Battery), readings.Battery.Present),
        };

        Surface.SetState("label", label);
        Surface.SetState("icon", new JsonObject { ["named"] = icon });
        Surface.SetState("tooltip", tooltip);
        Surface.SetState("visible", visible);
    }
}
