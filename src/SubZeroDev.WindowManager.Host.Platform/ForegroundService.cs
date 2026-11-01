using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>Grants foreground rights to workers and activates or minimises top-level windows.</summary>
public sealed class ForegroundService : IForegroundService
{
    public void AllowForeground(int processId) => Native.AllowSetForegroundWindow(processId);

    public Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct) => Task.Run(() => Activate(hwnd), ct);

    public Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct) => Task.Run(() =>
    {
        if (!Native.IsWindow(hwnd))
        {
            return Gone(hwnd);
        }

        Native.ShowWindow(hwnd, Native.SwMinimize);
        return Outcome.Success;
    }, ct);

    private static Outcome Gone(nint hwnd) => Outcome.Fail(CoreErrorCode.WindowGone, $"Window 0x{hwnd:X} no longer exists.");

    private static unsafe Outcome Activate(nint hwnd)
    {
        if (!Native.IsWindow(hwnd))
        {
            return Gone(hwnd);
        }

        if (Native.IsIconic(hwnd))
        {
            Native.ShowWindow(hwnd, Native.SwRestore);
        }

        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd)
        {
            return Outcome.Success;
        }

        // A lone Alt press makes this process the last one to receive input, which the foreground lock accepts.
        var inputs = stackalloc KeyboardInput[2];
        inputs[0] = new KeyboardInput { Type = Native.InputKeyboard, VirtualKey = Native.VkMenu };
        inputs[1] = new KeyboardInput { Type = Native.InputKeyboard, VirtualKey = Native.VkMenu, Flags = Native.KeyEventFKeyUp };
        Native.SendInput(2, inputs, sizeof(KeyboardInput));
        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd)
        {
            return Outcome.Success;
        }

        var current = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var self = Native.GetCurrentThreadId();
        if (current != 0 && current != self && Native.AttachThreadInput(self, current, true))
        {
            try
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                Native.AttachThreadInput(self, current, false);
            }
        }

        if (Native.GetForegroundWindow() == hwnd)
        {
            return Outcome.Success;
        }

        var flash = new FlashWInfo
        {
            Size = (uint)sizeof(FlashWInfo),
            Window = hwnd,
            Flags = Native.FlashwAll | Native.FlashwTimerNoFg,
        };
        Native.FlashWindowEx(ref flash);
        return Outcome.Fail(CoreErrorCode.ForegroundDenied, $"Windows refused to activate 0x{hwnd:X}; its taskbar button flashes instead.");
    }
}
