using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Recovery;

/// <summary>Undoes journalled desktop changes with plain Win32 calls; no UI and no host state.</summary>
public static partial class Reverter
{
    /// <summary>Newest first. Returns the entries that could not be reverted; the caller logs them and still clears the journal.</summary>
    public static IReadOnlyList<(DesktopChange Change, RecoveryError Error)> RevertAll(JournalDocument doc) => RevertAll(doc, Revert);

    internal static IReadOnlyList<(DesktopChange Change, RecoveryError Error)> RevertAll(JournalDocument doc, Func<DesktopChange, Outcome> revert)
    {
        var failures = new List<(DesktopChange, RecoveryError)>();
        for (var i = doc.Entries.Count - 1; i >= 0; i--)
        {
            var change = doc.Entries[i];
            if (revert(change).Error is { } error)
            {
                failures.Add((change, error));
            }
        }

        return failures;
    }

    public static Outcome Revert(DesktopChange change) => change.Kind switch
    {
        DesktopChangeKind.AppBar => RevertAppBar(change.Payload),
        DesktopChangeKind.WorkArea => RevertWorkArea(change.Payload),
        _ => Outcome.Fail(RecoveryErrorCode.RevertFailed, $"Unknown change kind {change.Kind}."),
    };

    /// <summary>Removes the registration for the window and lets the shell recompute the work area. Removing a dead window's registration is harmless.</summary>
    private static unsafe Outcome RevertAppBar(JsonObject payload)
    {
        if (!TryLong(payload, "hwnd", out var hwnd) || hwnd == 0)
        {
            return Outcome.Fail(RecoveryErrorCode.RevertFailed, "The app-bar entry has no window handle.");
        }

        var data = new AppBarData { Size = (uint)sizeof(AppBarData), Window = (nint)hwnd };
        SHAppBarMessage(AbmRemove, ref data);
        return Outcome.Success;
    }

    private static Outcome RevertWorkArea(JsonObject payload)
    {
        if (!TryLong(payload, "left", out var left) || !TryLong(payload, "top", out var top)
            || !TryLong(payload, "right", out var right) || !TryLong(payload, "bottom", out var bottom))
        {
            return Outcome.Fail(RecoveryErrorCode.RevertFailed, "The work-area entry has no rectangle.");
        }

        var rect = new Rect { Left = (int)left, Top = (int)top, Right = (int)right, Bottom = (int)bottom };
        return SystemParametersInfoW(SpiSetWorkArea, 0, ref rect, SpifSendChange)
            ? Outcome.Success
            : Outcome.Fail(RecoveryErrorCode.RevertFailed, $"SPI_SETWORKAREA failed with {Marshal.GetLastPInvokeError()}.");
    }

    private static bool TryLong(JsonObject payload, string name, out long value)
    {
        value = 0;
        return payload[name] is JsonValue node && node.TryGetValue(out value);
    }

    private const uint AbmRemove = 1;
    private const uint SpiSetWorkArea = 0x002F;
    private const uint SpifSendChange = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public nint Param;
    }

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, ref Rect rect, uint winIni);
}
