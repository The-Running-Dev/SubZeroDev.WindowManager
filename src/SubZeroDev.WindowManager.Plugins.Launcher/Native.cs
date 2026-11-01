using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SubZeroDev.WindowManager.Plugins.Launcher;

/// <summary>The shell's COM types for reading shortcuts, listing <c>shell:AppsFolder</c> and starting packaged apps.</summary>
internal static class Native
{
    public const int ASFW_ANY = -1;
    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    public const int S_OK = 0;
    public const int MaxPath = 1024;

    public static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    public static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, nint bindContext, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLink;

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    public class ApplicationActivationManager;

    /// <summary>Only the members up to <c>GetArguments</c>; COM needs the order, not the rest.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, nint findData, uint flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    public interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);

        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        void GetDisplayName(uint sigdn, out nint name);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    public interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    public interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }

    public static IShellLinkW LoadShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(path, 0);
        return link;
    }

    public static string DisplayName(IShellItem item, uint sigdn)
    {
        item.GetDisplayName(sigdn, out var name);
        try
        {
            return Marshal.PtrToStringUni(name) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }
}
