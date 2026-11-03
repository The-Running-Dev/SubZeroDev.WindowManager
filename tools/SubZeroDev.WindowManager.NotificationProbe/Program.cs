using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

// Answers: can an unpackaged process read other applications' notifications, and does one it raises reach them?
// Prints counts only, never the content of the user's notifications.
// Also kept next to the exe, for a run activated through the package, whose console closes when it ends.
var log = Path.Combine(AppContext.BaseDirectory, "probe-output.txt");
File.WriteAllText(log, "");
void Report(string key, object? value)
{
    Console.WriteLine($"{key}: {value}");
    File.AppendAllText(log, $"{key}: {value}\n");
}

Report("explorerRunning", Process.GetProcessesByName("explorer").Length > 0);
try
{
    Report("packageIdentity", Windows.ApplicationModel.Package.Current.Id.FamilyName);
}
catch (InvalidOperationException)
{
    Report("packageIdentity", "none");
}

var listener = UserNotificationListener.Current;
var access = listener.GetAccessStatus();
Report("accessBefore", access);
if (access != UserNotificationListenerAccessStatus.Allowed)
{
    try
    {
        // Windows shows the consent prompt only for a request made from a visible window's thread, with a message loop.
        var ui = Windows.System.DispatcherQueueController.CreateOnDedicatedThread();
        var requested = new TaskCompletionSource<UserNotificationListenerAccessStatus>();
        ui.DispatcherQueue.TryEnqueue(async () =>
        {
            var window = Native.CreateWindowEx(0, "STATIC", "SubZeroDev notification probe",
                Native.WsOverlappedWindow | Native.WsVisible, 200, 200, 480, 160, 0, 0, 0, 0);
            Native.SetForegroundWindow(window);
            try { requested.SetResult(await listener.RequestAccessAsync()); }
            catch (Exception e) { requested.SetException(e); }
            finally { Native.DestroyWindow(window); }
        });
        access = await requested.Task.WaitAsync(TimeSpan.FromMinutes(2));
        Report("accessRequested", access);
        await ui.ShutdownQueueAsync();
    }
    catch (Exception e)
    {
        Report("requestAccessError", $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message.Trim()}");
    }
}

try
{
    listener.NotificationChanged += (_, _) => { };
    Report("notificationChangedEvent", "subscribed");
}
catch (Exception e)
{
    Report("notificationChangedEvent", $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message.Trim()}");
}

var readFailed = false;
async Task<IReadOnlyList<UserNotification>> Read()
{
    try
    {
        return await listener.GetNotificationsAsync(NotificationKinds.Toast);
    }
    catch (Exception e)
    {
        if (!readFailed) Report("readError", $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message.Trim()}");
        readFailed = true;
        return [];
    }
}

var before = await Read();
Report("existingToasts", before.Count);
Report("existingApps", before.Select(n => n.AppInfo?.AppUserModelId).Distinct().Count());

var manager = AppNotificationManager.Default;
try
{
    manager.Register();
    var marker = Guid.NewGuid().ToString("N");
    var toast = new AppNotificationBuilder().AddText("SubZeroDev probe").AddText(marker).BuildNotification();
    // --visible shows the pop-up, which may be what puts a notification in the history.
    toast.SuppressDisplay = !args.Contains("--visible");
    toast.Tag = "probe";
    manager.Show(toast);
    Report("ownShown", toast.Id != 0);

    UserNotification? seen = null;
    var clock = Stopwatch.StartNew();
    while (seen is null && clock.Elapsed < TimeSpan.FromSeconds(5))
    {
        await Task.Delay(200);
        seen = (await Read()).FirstOrDefault(n =>
            n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements().Any(t => t.Text == marker) == true);
    }

    Report("ownSeenByListener", seen is not null ? $"after {clock.ElapsedMilliseconds} ms" : "no");
    if (seen is not null)
    {
        Report("ownAppId", seen.AppInfo?.AppUserModelId);
        Report("ownAppName", seen.AppInfo?.DisplayInfo?.DisplayName);
    }

    await manager.RemoveAllAsync();
}
catch (Exception e)
{
    Report("ownError", $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message.Trim()}");
}
finally
{
    try { manager.Unregister(); } catch (Exception e) { Report("unregisterError", e.Message.Trim()); }
}

static class Native
{
    public const uint WsOverlappedWindow = 0x00CF0000;
    public const uint WsVisible = 0x10000000;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint window);
}
