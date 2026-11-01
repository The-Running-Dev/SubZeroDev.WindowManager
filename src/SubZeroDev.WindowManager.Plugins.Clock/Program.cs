using System.Globalization;
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

/// <summary>Shows <c>HH:mm</c> and updates it just after each minute boundary.</summary>
public sealed class ClockInstance(InstanceRequest request) : ContributionInstance(request)
{
    private Timer? _timer;

    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Text("time", Prop<string>.State("time"), "clock"));
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

    private void Tick(object? _)
    {
        var now = DateTime.Now;
        Surface.SetState("time", now.ToString("HH:mm", CultureInfo.InvariantCulture));
        var next = now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1).AddMilliseconds(50) - now;
        if (_timer is null)
        {
            _timer = new Timer(Tick, null, next, Timeout.InfiniteTimeSpan);
        }
        else
        {
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
