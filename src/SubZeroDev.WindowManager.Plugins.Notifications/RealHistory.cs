using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace SubZeroDev.WindowManager.Plugins.Notifications;

/// <summary>
/// Windows' notification history through the user notification listener. It works only for a process with package
/// identity and the user's consent, both given outside the shell (S24.9): this class reports the state and never changes
/// it. It also never writes the account's notifications switch; it reads it.
/// </summary>
public sealed class RealHistory : INotificationHistory
{
    private static readonly TimeSpan Resync = TimeSpan.FromSeconds(60);
    private const int NoPackage = 15700;

    private readonly Timer _timer;
    private UserNotificationListener? _listener;
    private bool _subscribed;

    public RealHistory()
    {
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Resync, Resync);
    }

    public event EventHandler? Changed;

    public async Task<HistorySnapshot> ReadAsync(CancellationToken ct)
    {
        var switchedOn = SwitchedOn();
        if (!HasIdentity())
        {
            return new HistorySnapshot("none", "unspecified", switchedOn, []);
        }

        UserNotificationListener listener;
        string access;
        try
        {
            listener = Listener();
            access = listener.GetAccessStatus() switch
            {
                UserNotificationListenerAccessStatus.Allowed => "allowed",
                UserNotificationListenerAccessStatus.Denied => "denied",
                _ => "unspecified",
            };
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new HistoryException(ErrorNames.HistoryReadFailed, $"The notification listener is unavailable (0x{e.HResult:X8}).", retryable: true);
        }

        if (access != "allowed")
        {
            return new HistorySnapshot("ok", access, switchedOn, []);
        }

        try
        {
            var found = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct).ConfigureAwait(false);
            var items = new List<NotificationItem>(found.Count);
            foreach (var notification in found)
            {
                items.Add(Describe(notification));
            }

            items.Sort((a, b) => b.At.CompareTo(a.At));
            return new HistorySnapshot("ok", access, switchedOn, items);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The message is Windows' own and carries no notification text, but only the code is kept (I31).
            throw new HistoryException(ErrorNames.HistoryReadFailed, $"Reading the notification history failed (0x{e.HResult:X8}).", retryable: true);
        }
    }

    public Task DismissAsync(string id, CancellationToken ct)
    {
        if (uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            Listener().RemoveNotification(number);
        }

        return Task.CompletedTask;
    }

    public async Task ClearAsync(string? appId, CancellationToken ct)
    {
        var listener = Listener();
        if (appId is null)
        {
            listener.ClearNotifications();
            return;
        }

        var found = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct).ConfigureAwait(false);
        foreach (var notification in found.Where(n => n.AppInfo?.AppUserModelId == appId))
        {
            listener.RemoveNotification(notification.Id);
        }
    }

    public async Task ActivateAsync(string id, CancellationToken ct)
    {
        string? aumid = null;
        if (uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var found = await Listener().GetNotificationsAsync(NotificationKinds.Toast).AsTask(ct).ConfigureAwait(false);
            aumid = found.FirstOrDefault(n => n.Id == number)?.AppInfo?.AppUserModelId;
        }

        if (string.IsNullOrEmpty(aumid))
        {
            throw new HistoryException(ErrorNames.ActivationFailed, "That notification is no longer in the history.");
        }

        try
        {
            // The listener does not expose a notification's launch string, so this opens the application, not its content.
            AllowSetForegroundWindow(-1);
            ((IApplicationActivationManager)new ApplicationActivationManager()).ActivateApplication(aumid, null, 0, out _);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            throw new HistoryException(ErrorNames.ActivationFailed, $"The application could not be started (0x{e.HResult:X8}).");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_subscribed && _listener is not null)
        {
            try { _listener.NotificationChanged -= OnNotificationChanged; } catch (Exception e) when (e is COMException or InvalidOperationException) { }
        }
    }

    private UserNotificationListener Listener()
    {
        if (_listener is null)
        {
            _listener = UserNotificationListener.Current;
            try
            {
                // Not every configuration delivers the event; the minute resync covers it.
                _listener.NotificationChanged += OnNotificationChanged;
                _subscribed = true;
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                _subscribed = false;
            }
        }

        return _listener;
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args) =>
        Changed?.Invoke(this, EventArgs.Empty);

    private static NotificationItem Describe(UserNotification notification)
    {
        var texts = new List<string>();
        try
        {
            if (notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric) is { } binding)
            {
                texts.AddRange(binding.GetTextElements().Select(element => element.Text));
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            // A notification without readable text still appears, with its application's name.
        }

        var app = notification.AppInfo;
        var name = string.Empty;
        try { name = app?.DisplayInfo?.DisplayName ?? string.Empty; } catch (Exception e) when (e is COMException or InvalidOperationException) { }
        return new NotificationItem(
            notification.Id.ToString(CultureInfo.InvariantCulture),
            app?.AppUserModelId ?? string.Empty,
            name,
            texts.Count > 0 ? texts[0] : string.Empty,
            texts.Count > 1 ? string.Join('\n', texts.Skip(1)) : string.Empty,
            notification.CreationTime,
            Silent: false);
    }

    private static bool HasIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != NoPackage;
    }

    /// <summary>The account's "Notifications" switch; read, never written. Absent means on, as Windows treats it.</summary>
    private static bool SwitchedOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            return key?.GetValue("ToastEnabled") is not int enabled || enabled != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }
}
