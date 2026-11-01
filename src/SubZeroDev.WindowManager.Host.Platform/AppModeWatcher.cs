using Microsoft.Win32;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Follows the Windows app mode (light or dark) by watching <c>AppsUseLightTheme</c> under the current
/// user's personalisation key. <see cref="Changed"/> is raised on a background thread each time the
/// mode flips. A missing key or value reads as light, the Windows default.
/// </summary>
public sealed class AppModeWatcher : IDisposable
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string ValueName = "AppsUseLightTheme";

    private static readonly TimeSpan MissingKeyRetry = TimeSpan.FromSeconds(1);

    private readonly string _keyPath;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private volatile bool _isDark;
    private int _disposed;

    public AppModeWatcher(string? keyPath = null)
    {
        _keyPath = string.IsNullOrEmpty(keyPath) ? DefaultKeyPath : keyPath;
        _isDark = ReadIsDark(_keyPath);
    }

    public bool IsDark => _isDark;

    public event EventHandler? Changed;

    /// <summary>Starts watching. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Watch) { IsBackground = true, Name = "AppModeWatcher" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop.Set();
        // A thread still inside its wait would touch a disposed event, so then the event is left to the finaliser.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _stop.Dispose();
        }
    }

    public static bool ReadIsDark(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ValueName) is int light && light == 0;
    }

    private void Watch()
    {
        using var changed = new AutoResetEvent(false);
        while (!_stop.WaitOne(0))
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            if (key is null)
            {
                // The key may appear later (a test key, or a fresh profile); poll until it does.
                if (_stop.WaitOne(MissingKeyRetry))
                {
                    return;
                }

                Report();
                continue;
            }

            // The notification fires once, so it is armed again on every pass. Reading after arming
            // means a change between the two is never missed.
            var armed = Native.RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), 0, Native.RegNotifyChangeLastSet,
                changed.SafeWaitHandle.DangerousGetHandle(), 1) == 0;
            Report();
            var signalled = WaitHandle.WaitAny([changed, _stop], armed ? Timeout.InfiniteTimeSpan : MissingKeyRetry);
            if (signalled == 1)
            {
                return;
            }
        }
    }

    private void Report()
    {
        var dark = ReadIsDark(_keyPath);
        if (dark == _isDark)
        {
            return;
        }

        _isDark = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
