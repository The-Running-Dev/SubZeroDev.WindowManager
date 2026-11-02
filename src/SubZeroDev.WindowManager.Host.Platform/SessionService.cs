using System.Runtime.InteropServices;
using System.Text;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Lock, sign out, restart, shut down and sleep. Sign out, restart and shut down ask Windows politely
/// (no forcing), so an application with unsaved work may refuse: that is <c>SessionCommandRefused</c>.
/// </summary>
public sealed class SessionService : ISessionService
{
    public Outcome Lock() => LockWorkStation() ? Outcome.Success : Failed(CoreErrorCode.CommandFailed, "lock");

    public Outcome SignOut() => Exit(EwxLogOff, "sign out");

    public Outcome Restart() => Exit(EwxReboot, "restart");

    public Outcome Shutdown() => Exit(EwxPowerOff, "shut down");

    public Outcome Sleep()
    {
        EnableShutdownPrivilege();
        return SetSuspendState(false, false, false) ? Outcome.Success : Failed(CoreErrorCode.CommandFailed, "sleep");
    }

    private static Outcome Exit(uint how, string name)
    {
        if (how != EwxLogOff && !EnableShutdownPrivilege())
        {
            return Failed(CoreErrorCode.CommandFailed, name);
        }

        return ExitWindowsEx(how, ShtdnReasonPlanned) ? Outcome.Success : Failed(CoreErrorCode.SessionCommandRefused, name);
    }

    private static Outcome Failed(CoreErrorCode code, string name) =>
        Outcome.Fail(code, $"Windows did not {name} (error {Marshal.GetLastPInvokeError()}).");

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint EwxLogOff = 0x0;
    private const uint EwxReboot = 0x2;
    private const uint EwxPowerOff = 0x8;
    private const uint ShtdnReasonPlanned = 0x80000000;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, uint length, nint previous, nint returned);
}

/// <summary>
/// The session commands under the test hook: nothing reaches Windows. Each call is recorded as
/// <c>session-&lt;name&gt;</c> in <c>desktop-actions.log</c>, and a name listed in the hook file
/// <c>session-refuse</c> is refused as an application would refuse it.
/// </summary>
public sealed class RecordingSessionService(string hookDirectory) : ISessionService
{
    public const string RefuseFile = "session-refuse";

    public Outcome Lock() => Record("lock");

    public Outcome SignOut() => Record("signOut");

    public Outcome Restart() => Record("restart");

    public Outcome Shutdown() => Record("shutdown");

    public Outcome Sleep() => Record("sleep");

    private Outcome Record(string name)
    {
        Directory.CreateDirectory(hookDirectory);
        var refuse = Path.Combine(hookDirectory, RefuseFile);
        if (File.Exists(refuse) && File.ReadAllLines(refuse).Any(l => l.Trim() == name))
        {
            File.AppendAllText(Path.Combine(hookDirectory, Recovery.Reverter.DesktopActionsLog), $"session-{name} refused\n", new UTF8Encoding(false));
            return Outcome.Fail(CoreErrorCode.SessionCommandRefused, $"An application refused to let the session {name}.");
        }

        File.AppendAllText(Path.Combine(hookDirectory, Recovery.Reverter.DesktopActionsLog), $"session-{name}\n", new UTF8Encoding(false));
        return Outcome.Success;
    }
}
