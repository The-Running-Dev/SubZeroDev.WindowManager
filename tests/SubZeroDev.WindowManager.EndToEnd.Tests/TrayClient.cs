using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// A notification-area client in the test process: a hidden top-level window that sends what
/// <c>Shell_NotifyIcon</c> and <c>SHAppBarMessage</c> send, straight to the host's hook-scoped tray window, and
/// records the callbacks it gets back. The real shell functions and the real tray are never used.
/// </summary>
internal sealed unsafe partial class TrayClient : IDisposable
{
    public const uint CallbackMessage = 0x8001;
    public const uint Signature = 0x34753423;
    public const int NotifyDataSize = 956;

    private const string ClassName = "SubZeroDev.WindowManager.Tests.TrayClient";
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmCopyData = 0x004A;
    private const uint AbortIfHung = 0x0002;

    private static readonly ConcurrentDictionary<nint, TrayClient> Clients = new();
    private static readonly Lock RegisterGate = new();
    private static bool _registered;

    private readonly string _trayClass;
    private readonly uint _createdMessage;
    private readonly ConcurrentQueue<Received> _received = new();
    private readonly Thread _thread;

    private TrayClient(string trayClass, string? createdMessageName)
    {
        _trayClass = trayClass;
        _createdMessage = createdMessageName is null ? 0 : RegisterWindowMessageW(createdMessageName);
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "TrayClient" };
        _thread.Start();
        ready.Wait();
        if (Window == 0)
        {
            throw new InvalidOperationException("The test tray client's window was not created.");
        }
    }

    public readonly record struct Received(uint Message, nuint WParam, nint LParam);

    public nint Window { get; private set; }

    /// <summary>The key the host gives an icon of this window: its handle and id.</summary>
    public string Key(uint uid) => $"{(long)Window}:{uid}";

    public IReadOnlyList<Received> Messages => [.. _received];

    /// <summary>Whether the host's <c>TaskbarCreated</c> (under its hook-scoped name) has arrived.</summary>
    public bool HeardCreated => _createdMessage != 0 && _received.Any(m => m.Message == _createdMessage);

    /// <summary>Opens a client that sends to the tray window of <paramref name="trayClass"/>.</summary>
    public static TrayClient Open(string trayClass, string? createdMessageName = null) => new(trayClass, createdMessageName);

    /// <summary>The hook-scoped tray class and created-message names for a <c>tray-id</c>.</summary>
    public static (string ClassName, string CreatedMessage) Names(string id) =>
        ($"SubZeroDev.WindowManager.TrayWnd.{id}", $"SubZeroDev.WindowManager.TaskbarCreated.{id}");

    /// <summary>Sends one <c>Shell_NotifyIcon</c> message; the host's answer, 1 or 0.</summary>
    public nint Notify(uint message, uint uid, uint flags, string? tip = null, uint timeoutOrVersion = 0,
        string? infoTitle = null, string? info = null, uint state = 0, uint stateMask = 0)
    {
        var payload = new byte[8 + NotifyDataSize];
        var span = payload.AsSpan();
        BitConverter.TryWriteBytes(span, Signature);
        BitConverter.TryWriteBytes(span[4..], message);
        var nid = span[8..];
        BitConverter.TryWriteBytes(nid, NotifyDataSize);
        BitConverter.TryWriteBytes(nid[4..], (int)Window);
        BitConverter.TryWriteBytes(nid[8..], uid);
        BitConverter.TryWriteBytes(nid[12..], flags);
        BitConverter.TryWriteBytes(nid[16..], CallbackMessage);
        BitConverter.TryWriteBytes(nid[20..], (int)LoadIconW(0, 32512));
        WriteText(nid.Slice(24, 256), tip);
        BitConverter.TryWriteBytes(nid[280..], state);
        BitConverter.TryWriteBytes(nid[284..], stateMask);
        WriteText(nid.Slice(288, 512), info);
        BitConverter.TryWriteBytes(nid[800..], timeoutOrVersion);
        WriteText(nid.Slice(804, 128), infoTitle);
        return Send(1, payload);
    }

    /// <summary>Sends <paramref name="payload"/> as it is, with the given copy-data kind.</summary>
    public nint Send(nuint kind, byte[] payload)
    {
        var tray = FindWindowW(_trayClass, null);
        if (tray == 0)
        {
            throw new InvalidOperationException($"No window of class {_trayClass}.");
        }

        fixed (byte* data = payload)
        {
            var copy = new CopyData { Kind = kind, Size = (uint)payload.Length, Data = (nint)data };
            if (SendMessageTimeoutW(tray, WmCopyData, (nuint)Window, (nint)(&copy), AbortIfHung, 5000, out var result) == 0)
            {
                throw new InvalidOperationException("The tray window did not answer WM_COPYDATA.");
            }

            return (nint)result;
        }
    }

    /// <summary>
    /// Sends one <c>SHAppBarMessage</c> as the shell does: the <c>APPBARDATA</c> in memory shared with the tray,
    /// which writes the edge and rectangle back there.
    /// </summary>
    public (nint Result, int Edge, (int Left, int Top, int Right, int Bottom) Rect) AppBar(uint message, uint edge = 0,
        (int Left, int Top, int Right, int Bottom) rect = default, uint callback = 0)
    {
        const int AppBarDataSize = 40;
        var abd = new byte[AppBarDataSize];
        var span = abd.AsSpan();
        BitConverter.TryWriteBytes(span, AppBarDataSize);
        BitConverter.TryWriteBytes(span[4..], (int)Window);
        BitConverter.TryWriteBytes(span[8..], callback);
        BitConverter.TryWriteBytes(span[12..], edge);
        BitConverter.TryWriteBytes(span[16..], rect.Left);
        BitConverter.TryWriteBytes(span[20..], rect.Top);
        BitConverter.TryWriteBytes(span[24..], rect.Right);
        BitConverter.TryWriteBytes(span[28..], rect.Bottom);

        // Allocated into the tray's process, as shell32 does: a block allocated for the calling process stays
        // in its own heap and cannot be locked from anywhere else.
        var pid = (uint)Environment.ProcessId;
        GetWindowThreadProcessId(FindWindowW(_trayClass, null), out var trayPid);
        nint shared;
        fixed (byte* data = abd)
        {
            shared = SHAllocShared((nint)data, AppBarDataSize, trayPid);
        }

        if (shared == 0)
        {
            throw new InvalidOperationException("SHAllocShared failed.");
        }

        try
        {
            var payload = new byte[64];
            var message64 = payload.AsSpan();
            abd.CopyTo(message64);
            BitConverter.TryWriteBytes(message64[40..], message);
            BitConverter.TryWriteBytes(message64[48..], (long)shared);
            BitConverter.TryWriteBytes(message64[56..], pid);
            var result = Send(0, payload);

            var view = (int*)SHLockShared(shared, trayPid);
            if (view == null)
            {
                throw new InvalidOperationException("SHLockShared failed.");
            }

            try
            {
                return (result, view[3], (view[4], view[5], view[6], view[7]));
            }
            finally
            {
                SHUnlockShared((nint)view);
            }
        }
        finally
        {
            SHFreeShared(shared, trayPid);
        }
    }

    /// <summary>Destroys the window, as an app that exits without deleting its icons does.</summary>
    public void Close()
    {
        if (Window != 0 && IsWindow(Window))
        {
            PostMessageW(Window, WmClose, 0, 0);
        }

        _thread.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => Close();

    private static void WriteText(Span<byte> field, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 2)).CopyTo(field);
    }

    private void Loop(ManualResetEventSlim ready)
    {
        try
        {
            var module = GetModuleHandleW(null);
            lock (RegisterGate)
            {
                if (!_registered)
                {
                    fixed (char* name = ClassName)
                    {
                        var windowClass = new WindowClass
                        {
                            Size = (uint)sizeof(WindowClass),
                            Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&Procedure,
                            Instance = module,
                            ClassName = (nint)name,
                        };
                        _registered = RegisterClassExW(&windowClass) != 0;
                    }
                }
            }

            const uint ToolWindow = 0x00000080;
            const uint Popup = 0x80000000;
            Window = CreateWindowExW(ToolWindow, ClassName, ClassName, Popup, 0, 0, 0, 0, 0, 0, module, 0);
            if (Window != 0)
            {
                Clients[Window] = this;
            }
        }
        finally
        {
            ready.Set();
        }

        if (Window == 0)
        {
            return;
        }

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            DispatchMessageW(in message);
        }

        Clients.TryRemove(Window, out _);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Clients.TryGetValue(hwnd, out var client)
            && (message == CallbackMessage || (client._createdMessage != 0 && message == client._createdMessage)))
        {
            client._received.Enqueue(new Received(message, wParam, lParam));
            return 0;
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public nuint Kind;
        public uint Size;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeoutMs, out nuint result);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WindowClass* windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Message message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Message message);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("shlwapi.dll", EntryPoint = "#7")]
    private static partial nint SHAllocShared(nint data, uint size, uint processId);

    [LibraryImport("shlwapi.dll", EntryPoint = "#8")]
    private static partial nint SHLockShared(nint shared, uint processId);

    [LibraryImport("shlwapi.dll", EntryPoint = "#9")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHUnlockShared(nint view);

    [LibraryImport("shlwapi.dll", EntryPoint = "#10")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHFreeShared(nint shared, uint processId);
}
