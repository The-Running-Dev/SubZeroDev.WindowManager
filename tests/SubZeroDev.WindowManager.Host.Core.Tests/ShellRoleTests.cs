using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Recovery;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>
/// Slices S18.1 and S18.5: the shell roles, taken on a desktop the test creates (so the user's desktop never
/// gets a new shell window), and the replacement-mode work area against simulated monitors with the work
/// area recorded rather than set.
/// </summary>
public sealed class ShellRoleTests : IDisposable
{
    private const uint DesktopAll = 0x01FF;
    private const uint WsOverlappedWindow = 0x00CF0000;

    private readonly Scratch _scratch = new();
    private readonly List<string> _log = [];

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task S18_1_the_roles_are_held_and_the_hook_hears_a_window()
    {
        var desktop = CreateDesktopW("SubZeroDev.WindowManager.Tests." + Guid.NewGuid().ToString("N"), null, 0, 0, DesktopAll, 0);
        Assert.NotEqual(0, desktop);
        try
        {
            using var shell = ShellRoleService.Start(null, _log.Add, desktop);
            Assert.Equal(ShellRoleState.Held, shell.State);

            nint shellWindow = 0;
            var thread = new Thread(() =>
            {
                SetThreadDesktop(desktop);
                shellWindow = GetShellWindow();
            });
            thread.Start();
            thread.Join();
            Assert.Equal(shell.DesktopWindow, shellWindow);

            // Hook messages only reach hook windows on the input desktop, so the listener is on the process's
            // desktop (listening only: under the hook the shell window is never set there) and hears a window
            // that is never shown. Shown windows (created, flashing) are the end-to-end tests'.
            using var roles = ShellRoleService.Start(Path.Combine(_scratch.DataRoot, "hook"), _log.Add);
            Assert.Equal(ShellRoleState.Held, roles.State);
            Assert.NotEqual(0, roles.HookWindow);
            var window = CreateWindowExW(0, "Static", "S18.1 test window", WsOverlappedWindow, 0, 0, 200, 100, 0, 0, 0, 0);
            DestroyWindow(window);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && !Heard(roles, window, 2))
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            Assert.True(Heard(roles, window, 2), Describe(roles));
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [Fact]
    public void S18_2_another_shell_window_leaves_the_roles_alone()
    {
        var hook = Path.Combine(_scratch.DataRoot, "hook");
        Directory.CreateDirectory(hook);
        File.WriteAllText(Path.Combine(hook, "shell-owner"), "4242");

        using var roles = ShellRoleService.Start(hook, _log.Add);

        Assert.Equal(ShellRoleState.Taken, roles.State);
        Assert.Contains(_log, line => line.StartsWith("ShellRoleTaken", StringComparison.Ordinal) && line.Contains("4242", StringComparison.Ordinal));
        Assert.False(roles.SignalDesktopReady());
        Assert.False(roles.HideMinimizedWindows());
        Assert.False(File.Exists(Path.Combine(hook, Reverter.DesktopActionsLog)));
    }

    [Fact]
    public void S18_3_and_6_desktop_ready_is_signalled_once_and_minimised_windows_hide()
    {
        var hook = Path.Combine(_scratch.DataRoot, "hook");

        using var roles = ShellRoleService.Start(hook, _log.Add);

        Assert.Equal(ShellRoleState.Held, roles.State);
        Assert.True(roles.SignalDesktopReady());
        Assert.False(roles.SignalDesktopReady());
        Assert.True(roles.HideMinimizedWindows());
        Assert.Equal(["desktop-ready", "minimized-hide"], File.ReadAllLines(Path.Combine(hook, Reverter.DesktopActionsLog)));
    }

    [Fact]
    public async Task S18_5_reservations_take_strips_and_the_work_area_is_journalled_first()
    {
        var journal = new RecoveryJournal(Path.Combine(_scratch.DataRoot, "journal.json"), Environment.ProcessId, DateTimeOffset.UtcNow);
        await journal.StartAsync(TestContext.Current.CancellationToken);
        var monitor = new MonitorInfo("sim-a", 0, new RectInt(0, 0, 1000, 800), new RectInt(0, 0, 1000, 760), 96, true);
        var applied = new List<string>();
        using var service = new WorkAreaAppBarService(new FakeMonitors([monitor]), journal, (id, area) =>
        {
            // I6: the original is on disk before the work area changes.
            Assert.Contains(journal.Entries, entry => entry.Kind == DesktopChangeKind.WorkArea && entry.MonitorId == id);
            applied.Add($"{id} {area.Left},{area.Top},{area.Right},{area.Bottom}");
            return true;
        });
        var ct = TestContext.Current.CancellationToken;

        var top = await service.RegisterAsync(1, "sim-a", PanelEdge.Top, 30, ct);
        var left = await service.RegisterAsync(2, "sim-a", PanelEdge.Left, 40, ct);

        Assert.Equal(new RectInt(0, 0, 1000, 30), top.Value);
        Assert.Equal(new RectInt(0, 30, 40, 800), left.Value);
        var entry = Assert.Single(journal.Entries);
        Assert.Equal(760, (int)entry.Payload["bottom"]!);

        Assert.True((await service.RemoveAsync(1, ct)).Ok);
        Assert.Single(journal.Entries);
        Assert.True((await service.RemoveAsync(2, ct)).Ok);

        Assert.Empty(journal.Entries);
        Assert.Equal(["sim-a 0,30,1000,800", "sim-a 40,30,1000,800", "sim-a 40,0,1000,800", "sim-a 0,0,1000,760"], applied);
    }

    [Fact]
    public async Task S18_5_a_refused_work_area_is_not_left_in_the_journal()
    {
        var journal = new RecoveryJournal(Path.Combine(_scratch.DataRoot, "journal.json"), Environment.ProcessId, DateTimeOffset.UtcNow);
        var monitor = new MonitorInfo("sim-a", 0, new RectInt(0, 0, 1000, 800), new RectInt(0, 0, 1000, 800), 96, true);
        using var service = new WorkAreaAppBarService(new FakeMonitors([monitor]), journal, (_, _) => false);

        var result = await service.RegisterAsync(1, "sim-a", PanelEdge.Bottom, 30, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(CoreErrorCode.AppBarRefused, result.Error!.Code);
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public void S18_5_the_hook_applier_records_instead_of_setting()
    {
        var hook = Path.Combine(_scratch.DataRoot, "hook");

        Assert.True(WorkAreaAppBarService.Applier(hook)("sim-a", new RectInt(0, 30, 1000, 800)));

        Assert.Equal(["work-area sim-a 0,30,1000,800"], File.ReadAllLines(Path.Combine(hook, Reverter.DesktopActionsLog)));
    }

    private static bool Heard(ShellRoleService roles, nint window, int code) =>
        roles.Events.Any(e => e.Code == code && e.Hwnd == window);

    private string Describe(ShellRoleService roles) =>
        string.Join(", ", roles.Events.Select(e => $"{e.Code:x}:{e.Hwnd:x}")) + " | " + string.Join(" / ", _log);

    private sealed class FakeMonitors(IReadOnlyList<MonitorInfo> monitors) : IMonitorService
    {
        public IReadOnlyList<MonitorInfo> Monitors => monitors;

        public event EventHandler TopologyChanged { add { } remove { } }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateDesktopW(string name, string? device, nint mode, uint flags, uint access, nint attributes);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
