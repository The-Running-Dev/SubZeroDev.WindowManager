using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

// A memory-only substitute for the host transport, not native Windows or full IPC acceptance.
public sealed class ProbeHost(string root) : IAsyncDisposable
{
    private readonly Connection _connection = new(Stream.Null);

    public T CreatePlugin<T>() where T : Plugin, new()
    {
        var plugin = new T();
        plugin.Context = new PluginContext(_connection, "subzerodev.notifications", HostMode.Replacement, root, [],
            new HashSet<string>(["notifications.openCentre", "notifications.dismiss", "notifications.clear", "notifications.activate"]),
            new HashSet<string>(["notifications.history", "notifications.status"]),
            new HashSet<string>(["host.notifications.doNotDisturb"]),
            new HashSet<string>(["toast", "centre", "setup"]), action => _ = Task.Run(action));
        return plugin;
    }

    public Task StartAsync(Plugin plugin) => plugin.OnStartAsync(CancellationToken.None);
    public Task StopAsync(Plugin plugin) => plugin.OnStopAsync(CancellationToken.None);
    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
