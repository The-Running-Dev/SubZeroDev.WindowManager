using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Plugins.Notifications;

/// <summary>
/// The test hook's history (invariant I38): reads <c>notifications.json</c> in the hook directory and writes what the
/// shell does to <c>desktop-actions.log</c>, so a test drives it with files and touches nothing of the user's. The file is
/// <c>{ identity, access, switchedOn, readFails, items: [{ id, appId, appName, title, body, at, silent, activationFails }] }</c>;
/// every part is optional and an absent file is an empty, permitted history.
/// </summary>
public sealed class FakeHistory : INotificationHistory
{
    private readonly IdleDiagnostics? _diagnostics;
    private readonly string _file;
    private readonly string _actions;
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher _watcher;
    private (long Ticks, long Length) _stamp;

    public FakeHistory(string hookDirectory, IdleDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics;
        _file = Path.Combine(hookDirectory, "notifications.json");
        _actions = Path.Combine(hookDirectory, "desktop-actions.log");
        _stamp = Stamp();
        // Observe the fake's atomic file replacements instead of waking the worker ten times per second.
        // Match the real provider's event-driven observation without touching Windows notifications.
        _watcher = new FileSystemWatcher(hookDirectory, "notifications.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, _) => Observe();
        _watcher.Created += (_, _) => Observe();
        _watcher.Deleted += (_, _) => Observe();
        _watcher.Renamed += (_, _) => Observe();
        // An overflow invalidates the snapshot; read the authority again, without starting an idle poll loop.
        _watcher.Error += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _watcher.EnableRaisingEvents = true;
    }

    public event EventHandler? Changed;

    public Task<HistorySnapshot> ReadAsync(CancellationToken ct)
    {
        var started = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            return Read();
        }
        finally
        {
            _diagnostics?.Record("fake.read", started);
        }
    }

    private Task<HistorySnapshot> Read()
    {
        var document = Load();
        if (document?["readFails"] is JsonValue fails && fails.TryGetValue<bool>(out var failed) && failed)
        {
            throw new HistoryException(ErrorNames.HistoryReadFailed, "The fake history was told to fail the read.", retryable: true);
        }

        var identity = Text(document, "identity") ?? "ok";
        var access = Text(document, "access") ?? "allowed";
        var switchedOn = document?["switchedOn"] is not JsonValue on || !on.TryGetValue<bool>(out var value) || value;
        var items = new List<NotificationItem>();
        if (document?["items"] is JsonArray list)
        {
            lock (_gate)
            {
                foreach (var node in list.OfType<JsonObject>())
                {
                    var id = Text(node, "id") ?? string.Empty;
                    if (_removed.Contains(id))
                    {
                        continue;
                    }

                    items.Add(new NotificationItem(id, Text(node, "appId") ?? string.Empty, Text(node, "appName") ?? string.Empty,
                        Text(node, "title") ?? string.Empty, Text(node, "body") ?? string.Empty, At(node),
                        node["silent"] is JsonValue silent && silent.TryGetValue<bool>(out var quiet) && quiet));
                }
            }
        }

        items.Sort((a, b) => b.At.CompareTo(a.At));
        return Task.FromResult(new HistorySnapshot(identity, access, switchedOn, items));
    }

    public Task DismissAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            _removed.Add(id);
        }

        Record($"notification-dismiss {id}");
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task ClearAsync(string? appId, CancellationToken ct)
    {
        var snapshot = await ReadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var item in snapshot.Items.Where(i => appId is null || i.AppId == appId))
            {
                _removed.Add(item.Id);
            }
        }

        Record($"notification-clear {appId ?? "all"}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task ActivateAsync(string id, CancellationToken ct)
    {
        var item = Load()?["items"] is JsonArray list
            ? list.OfType<JsonObject>().FirstOrDefault(node => Text(node, "id") == id)
            : null;
        if (item is null)
        {
            throw new HistoryException(ErrorNames.ActivationFailed, "That notification is no longer in the history.");
        }

        if (item["activationFails"] is JsonValue fails && fails.TryGetValue<bool>(out var failed) && failed)
        {
            throw new HistoryException(ErrorNames.ActivationFailed, $"{Text(item, "appName")} could not be started.");
        }

        Record($"notification-activate {id} {Text(item, "appId")}");
        return Task.CompletedTask;
    }

    public void Dispose() => _watcher.Dispose();

    private static string? Text(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset At(JsonObject node) =>
        Text(node, "at") is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : DateTimeOffset.UnixEpoch;

    private JsonObject? Load()
    {
        // The test may be mid-write; the read retries below cover the finished file.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file, Encoding.UTF8)) as JsonObject : null;
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    private (long, long) Stamp()
    {
        var info = new FileInfo(_file);
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
    }

    private void Observe()
    {
        var started = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
        try
        {
            ObserveCore();
        }
        finally
        {
            _diagnostics?.Record("fake.observe", started);
        }
    }

    private void ObserveCore()
    {
        lock (_gate)
        {
            var stamp = Stamp();
            if (stamp == _stamp)
            {
                return;
            }

            _stamp = stamp;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Record(string line)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_actions)!);
            File.AppendAllText(_actions, line + "\n", new UTF8Encoding(false));
        }
    }
}
