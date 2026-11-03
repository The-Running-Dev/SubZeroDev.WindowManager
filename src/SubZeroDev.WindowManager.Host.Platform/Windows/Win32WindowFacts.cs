using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>Reads a window's facts from Win32. Read-only; it never changes a window.</summary>
public sealed unsafe class Win32WindowFacts : IWindowFacts
{
    private const int CacheLimit = 2048;
    private const long ProcessTtlMs = 5000;

    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid AumidFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private readonly Lock _gate = new();
    private readonly Dictionary<uint, ProcessInfo> _processes = [];
    private readonly Dictionary<nint, (WindowFingerprint Print, string? Aumid)> _aumids = [];

    private readonly record struct ProcessInfo(long StartTicks, string? Exe, long Stamp);

    public WindowFingerprint? Fingerprint(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd))
        {
            return null;
        }

        var thread = Native.GetWindowThreadProcessId(hwnd, out var processId);
        return thread == 0 ? null : new WindowFingerprint((int)processId, Process(processId).StartTicks, (int)thread, ClassOf(hwnd));
    }

    public WindowFacts? Read(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd))
        {
            return null;
        }

        var thread = Native.GetWindowThreadProcessId(hwnd, out var processId);
        if (thread == 0)
        {
            return null;
        }

        var process = Process(processId);
        var print = new WindowFingerprint((int)processId, process.StartTicks, (int)thread, ClassOf(hwnd));
        var exStyle = (long)Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        return new WindowFacts(
            print,
            TitleOf(hwnd),
            process.Exe,
            AumidOf(hwnd, print),
            Native.GetWindow(hwnd, Native.GwOwner),
            MonitorOf(hwnd),
            Native.IsWindowVisible(hwnd),
            CloakOf(hwnd),
            (exStyle & Native.WsExToolWindowStyle) != 0,
            (exStyle & Native.WsExNoActivate) != 0,
            (exStyle & Native.WsExAppWindow) != 0,
            Native.IsIconic(hwnd),
            processId == (uint)Environment.ProcessId,
            hwnd == Native.GetShellWindow());
    }

    private static string ClassOf(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = Native.GetClassName(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string TitleOf(nint hwnd)
    {
        var length = Native.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            return new string(text, 0, Math.Max(0, Native.GetWindowTextW(hwnd, text, buffer.Length)));
        }
    }

    private static string MonitorOf(nint hwnd)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        var monitor = Native.MonitorFromWindow(hwnd, Native.MonitorDefaultToNearest);
        return monitor != 0 && Native.GetMonitorInfo(monitor, &info) ? new string(info.Device).TrimEnd('\0') : string.Empty;
    }

    private static CloakState CloakOf(nint hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DwmwaCloaked, out var cloaked, sizeof(int)) != 0 || cloaked == 0)
        {
            return CloakState.None;
        }

        return (cloaked & 2) != 0 ? CloakState.Shell : (cloaked & 1) != 0 ? CloakState.App : CloakState.Inherited;
    }

    private ProcessInfo Process(uint processId)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_processes.TryGetValue(processId, out var cached) && now - cached.Stamp < ProcessTtlMs)
            {
                return cached;
            }
        }

        var read = ReadProcess(processId) with { Stamp = now };
        lock (_gate)
        {
            if (_processes.Count > CacheLimit)
            {
                _processes.Clear();
            }

            _processes[processId] = read;
        }

        return read;
    }

    private static ProcessInfo ReadProcess(uint processId)
    {
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, 0, processId);
        if (process == 0)
        {
            return new ProcessInfo(0, null, 0);
        }

        try
        {
            long creation = 0, exit = 0, kernel = 0, user = 0;
            var start = Native.GetProcessTimes(process, &creation, &exit, &kernel, &user) ? creation : 0;
            var buffer = stackalloc char[1024];
            uint size = 1024;
            var exe = Native.QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
            return new ProcessInfo(start, exe, 0);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>The application user model id of the window, read once per window identity.</summary>
    private string? AumidOf(nint hwnd, WindowFingerprint print)
    {
        lock (_gate)
        {
            if (_aumids.TryGetValue(hwnd, out var cached) && cached.Print == print)
            {
                return cached.Aumid;
            }
        }

        var aumid = ReadAumid(hwnd);
        lock (_gate)
        {
            if (_aumids.Count > CacheLimit)
            {
                _aumids.Clear();
            }

            _aumids[hwnd] = (print, aumid);
        }

        return aumid;
    }

    private static string? ReadAumid(nint hwnd)
    {
        nint store = 0;
        var iid = PropertyStoreId;
        if (Native.SHGetPropertyStoreForWindow(hwnd, &iid, &store) != 0 || store == 0)
        {
            return null;
        }

        try
        {
            var vtable = *(nint**)store;
            var getValue = (delegate* unmanaged[Stdcall]<nint, byte*, byte*, int>)vtable[5];
            var key = stackalloc byte[20];
            *(Guid*)key = AumidFormat;
            *(uint*)(key + 16) = 5;
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();
            if (getValue(store, key, variant) != 0)
            {
                return null;
            }

            try
            {
                // VT_LPWSTR
                return *(ushort*)variant == 31 && *(nint*)(variant + 8) is var text and not 0
                    ? System.Runtime.InteropServices.Marshal.PtrToStringUni(text) is { Length: > 0 } value ? value : null
                    : null;
            }
            finally
            {
                Native.PropVariantClear(variant);
            }
        }
        finally
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)store)[2];
            release(store);
        }
    }
}
