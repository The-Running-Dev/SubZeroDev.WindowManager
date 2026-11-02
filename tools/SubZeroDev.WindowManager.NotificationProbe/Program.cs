using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

// Answers: can an unpackaged process read other applications' notifications, and does one it raises reach them?
// Prints counts only, never the content of the user's notifications.
static void Report(string key, object? value) => Console.WriteLine($"{key}: {value}");

Report("explorerRunning", Process.GetProcessesByName("explorer").Length > 0);

var listener = UserNotificationListener.Current;
var access = listener.GetAccessStatus();
Report("accessBefore", access);
if (access != UserNotificationListenerAccessStatus.Allowed)
{
    try
    {
        access = await listener.RequestAccessAsync();
        Report("accessRequested", access);
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

async Task<IReadOnlyList<UserNotification>> Read()
{
    try
    {
        return await listener.GetNotificationsAsync(NotificationKinds.Toast);
    }
    catch (Exception e)
    {
        Report("readError", $"{e.GetType().Name} 0x{e.HResult:X8} {e.Message.Trim()}");
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
    toast.SuppressDisplay = true;
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
