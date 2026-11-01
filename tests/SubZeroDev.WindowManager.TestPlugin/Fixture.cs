using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SubZeroDev.WindowManager.TestPlugin;

/// <summary>
/// <c>fixture-windows &lt;token&gt;</c>: not a plugin. Opens one top-level window of each kind a window
/// list must judge, titled "&lt;token&gt; &lt;kind&gt;", prints "&lt;kind&gt; &lt;hwnd&gt;" for each and then "ready",
/// and pumps messages until it is killed. <c>normal</c> has a class icon; <c>plain</c> has none, and this
/// exe has no icon resource, so no icon can be found for it. The rest must never be listed:
/// <c>invisible</c>, <c>owned</c> (owned by <c>normal</c>), <c>tool</c>, <c>cloaked</c> and <c>untitled</c>.
/// </summary>
internal static unsafe partial class Fixture
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExToolWindow = 0x00000080;
    private const int SwShowNoActivate = 4;
    private const uint DwmwaCloak = 13;
    private const nint IdiInformation = 32516;

    public static int Run(string token)
    {
        var module = GetModuleHandleW(null);
        Register("SzdFixtureIcon", module, LoadIconW(0, IdiInformation));
        Register("SzdFixturePlain", module, 0);

        var normal = Create("SzdFixtureIcon", $"{token} normal", WsOverlappedWindow | WsVisible, 0, 0, module);
        var windows = new (string Kind, nint Hwnd)[]
        {
            ("normal", normal),
            ("plain", Create("SzdFixturePlain", $"{token} plain", WsOverlappedWindow | WsVisible, 0, 0, module)),
            ("invisible", Create("SzdFixtureIcon", $"{token} invisible", WsOverlappedWindow, 0, 0, module)),
            ("owned", Create("SzdFixtureIcon", $"{token} owned", WsOverlappedWindow | WsVisible, 0, normal, module)),
            ("tool", Create("SzdFixtureIcon", $"{token} tool", WsOverlappedWindow | WsVisible, WsExToolWindow, 0, module)),
            ("cloaked", Cloaked(Create("SzdFixtureIcon", $"{token} cloaked", WsOverlappedWindow, 0, 0, module))),
            ("untitled", Create("SzdFixtureIcon", string.Empty, WsOverlappedWindow | WsVisible, 0, 0, module)),
        };

        foreach (var (kind, hwnd) in windows)
        {
            Console.WriteLine($"{kind} {hwnd}");
        }

        Console.WriteLine("ready");
        Console.Out.Flush();
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }

        return 0;
    }

    private static nint Cloaked(nint hwnd)
    {
        var on = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, DwmwaCloak, &on, sizeof(int)));
        ShowWindow(hwnd, SwShowNoActivate);
        return hwnd;
    }

    private static void Register(string name, nint module, nint icon)
    {
        fixed (char* className = name)
        {
            var windowClass = new WindowClass
            {
                Size = (uint)sizeof(WindowClass),
                WindowProc = &WindowProc,
                Instance = module,
                Icon = icon,
                ClassName = className,
            };
            if (RegisterClassExW(&windowClass) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    private static nint Create(string className, string title, uint style, uint exStyle, nint owner, nint module)
    {
        var hwnd = CreateWindowExW(exStyle, className, title, style, 120, 120, 360, 240, owner, 0, module, 0);
        return hwnd != 0 ? hwnd : throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam) => DefWindowProcW(hwnd, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Hwnd;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint module, nint name);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WindowClass* windowClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Message message, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Message message);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
}
