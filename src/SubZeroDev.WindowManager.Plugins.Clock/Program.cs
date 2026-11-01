using System.Globalization;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.Clock;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<ClockPlugin>(args);
}

public sealed class ClockPlugin : Plugin
{
    protected override ContributionInstance CreateInstance(InstanceRequest request) => new ClockInstance(request);
}

/// <summary>
/// Shows the time in the instance's <c>format</c> (default <c>HH:mm</c>), with the date first when
/// <c>showDate</c> is set. It updates each second when the format shows seconds, else just after each minute.
/// </summary>
public sealed class ClockInstance(InstanceRequest request) : ContributionInstance(request)
{
    private const string DefaultFormat = "HH:mm";
    private readonly Lock _gate = new();
    private Timer? _timer;

    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Text("time", Prop<string>.State("time"), "clock"));
        Tick(null);
        return Task.CompletedTask;
    }

    protected override Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct)
    {
        Tick(null);
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private string Format => Settings["format"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
        ? text
        : DefaultFormat;

    private bool ShowDate => Settings["showDate"] is JsonValue value && value.TryGetValue<bool>(out var show) && show;

    private void Tick(object? _)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var format = Format;
            string text;
            try
            {
                text = now.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                format = DefaultFormat;
                text = now.ToString(format, CultureInfo.InvariantCulture);
            }

            Surface.SetState("time", ShowDate ? $"{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {text}" : text);
            var next = format.Contains('s', StringComparison.Ordinal)
                ? now.Date.AddHours(now.Hour).AddMinutes(now.Minute).AddSeconds(now.Second + 1).AddMilliseconds(50) - now
                : now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1).AddMilliseconds(50) - now;
            if (_timer is null)
            {
                _timer = new Timer(Tick, null, next, Timeout.InfiniteTimeSpan);
                return;
            }

            try
            {
                _timer.Change(next, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposed while this tick ran.
            }
        }
    }
}
