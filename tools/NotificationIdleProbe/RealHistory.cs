namespace SubZeroDev.WindowManager.Plugins.Notifications;

// Fail closed: a portable measurement must never claim to exercise the WinRT provider.
internal sealed class RealHistory : INotificationHistory
{
    public RealHistory() => throw new PlatformNotSupportedException("This probe supports only the simulated provider.");
    public event EventHandler? Changed { add { } remove { } }
    public Task<HistorySnapshot> ReadAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task DismissAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task ClearAsync(string? appId, CancellationToken ct) => throw new NotSupportedException();
    public Task ActivateAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public void Dispose() { }
}
