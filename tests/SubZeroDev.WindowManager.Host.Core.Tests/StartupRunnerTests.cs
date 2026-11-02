using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SubZeroDev.WindowManager.Host.Platform;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>
/// Slice S18.4: the start-up runner, against a test registry root under <c>HKCU</c> and test Startup folders.
/// The real Run keys and Startup folders are never read or written.
/// </summary>
public sealed class StartupRunnerTests : IDisposable
{
    private readonly string _root = @"Software\SubZeroDev.WindowManager.Tests\startup-" + Guid.NewGuid().ToString("N");
    private readonly Scratch _scratch = new();
    private readonly List<string> _started = [];
    private readonly List<string> _log = [];

    private string Common => Path.Combine(_scratch.DataRoot, "common");

    private string User => Path.Combine(_scratch.DataRoot, "user");

    public void Dispose()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_root, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey))
        {
            // A test may have denied itself write access; take it back before deleting.
            if (key is not null)
            {
                Allow(key);
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        _scratch.Dispose();
    }

    [Fact]
    public void S18_4_a_sources_run_in_explorers_order()
    {
        Set(@"HKLM\" + StartupRunner.RunOnceKey, "a", "c:\\lm\\runonce.exe");
        Set(@"HKLM\" + StartupRunner.RunOnceExKey + @"\0001", "1", "c:\\lm\\runonceex.exe /x");
        Set(@"HKLM\" + StartupRunner.RunKey, "a", "\"c:\\lm\\run.exe\" -min");
        Set(@"HKLM\" + StartupRunner.Run32Key, "a", "c:\\lm\\run32.exe");
        Set(@"HKCU\" + StartupRunner.RunKey, "a", "c:\\cu\\run.exe");
        Set(@"HKCU\" + StartupRunner.RunOnceKey, "a", "c:\\cu\\runonce.exe");
        Directory.CreateDirectory(Common);
        Directory.CreateDirectory(User);
        File.WriteAllText(Path.Combine(Common, "common.lnk"), string.Empty);
        File.WriteAllText(Path.Combine(User, "user.lnk"), string.Empty);
        File.WriteAllText(Path.Combine(User, "desktop.ini"), string.Empty);

        var report = Runner().Run();

        Assert.False(report.AlreadyRan);
        Assert.Equal(
        [
            "c:\\lm\\runonce.exe|",
            "c:\\lm\\runonceex.exe|/x",
            "c:\\lm\\run.exe|-min",
            "c:\\lm\\run32.exe|",
            "c:\\cu\\run.exe|",
            Path.Combine(Common, "common.lnk") + "|",
            Path.Combine(User, "user.lnk") + "|",
            "c:\\cu\\runonce.exe|",
        ], _started);
        Assert.Null(Get(@"HKLM\" + StartupRunner.RunOnceKey, "a"));
        Assert.Null(Get(@"HKCU\" + StartupRunner.RunOnceKey, "a"));
        using var runOnceEx = Registry.CurrentUser.OpenSubKey($@"{_root}\HKLM\{StartupRunner.RunOnceExKey}\0001");
        Assert.Null(runOnceEx);
        Assert.Equal("\"c:\\lm\\run.exe\" -min", Get(@"HKLM\" + StartupRunner.RunKey, "a"));
    }

    [Fact]
    public void S18_4_b_a_bang_value_is_deleted_only_after_a_successful_start()
    {
        Set(@"HKCU\" + StartupRunner.RunOnceKey, "!ok", "c:\\ok.exe");
        Set(@"HKCU\" + StartupRunner.RunOnceKey, "!fails", "c:\\fails.exe");
        Set(@"HKCU\" + StartupRunner.RunOnceKey, "fails", "c:\\fails.exe /plain");

        Runner(file => !file.Contains("fails", StringComparison.Ordinal)).Run();

        Assert.Null(Get(@"HKCU\" + StartupRunner.RunOnceKey, "!ok"));
        Assert.Equal("c:\\fails.exe", Get(@"HKCU\" + StartupRunner.RunOnceKey, "!fails"));
        Assert.Null(Get(@"HKCU\" + StartupRunner.RunOnceKey, "fails"));
    }

    [Fact]
    public void S18_4_c_an_hklm_value_that_cannot_be_deleted_is_left_alone_and_logged()
    {
        Set(@"HKLM\" + StartupRunner.RunOnceKey, "locked", "c:\\locked.exe");
        Set(@"HKLM\" + StartupRunner.RunOnceKey, "!locked", "c:\\locked-bang.exe");
        Set(@"HKLM\" + StartupRunner.RunKey, "run", "c:\\run.exe");
        using (var key = Registry.CurrentUser.OpenSubKey($@"{_root}\HKLM\{StartupRunner.RunOnceKey}", RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ChangePermissions | RegistryRights.ReadKey)!)
        {
            var security = key.GetAccessControl();
            security.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.SetValue, AccessControlType.Deny));
            key.SetAccessControl(security);
        }

        var report = Runner().Run();

        Assert.Equal(["c:\\run.exe|"], _started);
        Assert.Equal("c:\\locked.exe", Get(@"HKLM\" + StartupRunner.RunOnceKey, "locked"));
        Assert.Contains(@"HKLM\RunOnce\locked", report.Skipped);
        Assert.Contains(_log, line => line.Contains("'locked' could not be deleted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("HKLM", "DisableLocalMachineRun", "c:\\lm\\run.exe|")]
    [InlineData("HKCU", "DisableCurrentUserRun", "c:\\cu\\run.exe|")]
    [InlineData("HKLM", "DisableLocalMachineRunOnce", "c:\\lm\\runonce.exe|")]
    [InlineData("HKCU", "DisableCurrentUserRunOnce", "c:\\cu\\runonce.exe|")]
    public void S18_4_d_a_source_switched_off_by_policy_is_skipped(string policyHive, string policy, string missing)
    {
        Set(@"HKLM\" + StartupRunner.RunOnceKey, "a", "c:\\lm\\runonce.exe");
        Set(@"HKLM\" + StartupRunner.RunKey, "a", "c:\\lm\\run.exe");
        Set(@"HKCU\" + StartupRunner.RunKey, "a", "c:\\cu\\run.exe");
        Set(@"HKCU\" + StartupRunner.RunOnceKey, "a", "c:\\cu\\runonce.exe");
        using (var key = Registry.CurrentUser.CreateSubKey($@"{_root}\{policyHive}\{StartupRunner.PolicyKey}"))
        {
            key.SetValue(policy, 1, RegistryValueKind.DWord);
        }

        var report = Runner().Run();

        Assert.Equal(3, _started.Count);
        Assert.DoesNotContain(missing, _started);
        Assert.Contains(policy, report.Skipped);
    }

    [Fact]
    public void S18_4_e_entries_disabled_in_task_manager_are_skipped()
    {
        Set(@"HKCU\" + StartupRunner.RunKey, "off", "c:\\off.exe");
        Set(@"HKCU\" + StartupRunner.RunKey, "on", "c:\\on.exe");
        Directory.CreateDirectory(User);
        File.WriteAllText(Path.Combine(User, "off.lnk"), string.Empty);
        using (var key = Registry.CurrentUser.CreateSubKey($@"{_root}\HKCU\{StartupRunner.ApprovedKey}\Run"))
        {
            key.SetValue("off", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            key.SetValue("on", new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        using (var key = Registry.CurrentUser.CreateSubKey($@"{_root}\HKCU\{StartupRunner.ApprovedKey}\StartupFolder"))
        {
            key.SetValue("off.lnk", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Runner().Run();

        Assert.Equal(["c:\\on.exe|"], _started);
    }

    [Fact]
    public void S18_4_f_a_second_start_in_the_same_session_runs_nothing()
    {
        Set(@"HKCU\" + StartupRunner.RunKey, "a", "c:\\cu\\run.exe");

        Assert.False(Runner().Run().AlreadyRan);
        var second = Runner().Run();

        Assert.True(second.AlreadyRan);
        Assert.Single(_started);
    }

    [Theory]
    [InlineData("\"c:\\program files\\app.exe\" -a -b", "c:\\program files\\app.exe", "-a -b")]
    [InlineData("c:\\tools\\app.exe /min", "c:\\tools\\app.exe", "/min")]
    [InlineData("app", "app", "")]
    public void S18_4_g_a_run_command_splits_into_file_and_arguments(string command, string file, string arguments) =>
        Assert.Equal((file, arguments), StartupRunner.Split(command));

    private StartupRunner Runner(Func<string, bool>? succeeds = null) =>
        new(new StartupSources(_root, Common, User), (file, arguments) =>
        {
            var ok = succeeds?.Invoke(file) ?? true;
            if (ok)
            {
                _started.Add($"{file}|{arguments}");
            }

            return ok;
        }, _log.Add);

    private void Set(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_root}\{path}");
        key.SetValue(name, value);
    }

    private string? Get(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_root}\{path}");
        return key?.GetValue(name) as string;
    }

    private static void Allow(RegistryKey key)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey);
            if (child is not null)
            {
                Allow(child);
            }
        }

        var security = key.GetAccessControl();
        security.RemoveAccessRuleAll(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!, RegistryRights.SetValue, AccessControlType.Deny));
        key.SetAccessControl(security);
    }
}
