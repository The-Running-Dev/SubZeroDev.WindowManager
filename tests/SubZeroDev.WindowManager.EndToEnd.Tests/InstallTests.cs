using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S15: the guardian's per-user install registration. The Start-menu folder and the Run key are
/// pointed at a scratch folder and a scratch <c>HKCU</c> key, so the real ones are never touched.
/// </summary>
public sealed class InstallTests
{
    private const string StartMenuVariable = "SUBZERODEV_WM_START_MENU_DIR";
    private const string RunKeyVariable = "SUBZERODEV_WM_RUN_KEY";
    private const string RunValueName = "SubZeroDev.WindowManager";
    private const string ShortcutName = "SubZeroDev.WindowManager.lnk";
    private const string TestKeyRoot = @"Software\SubZeroDev.WindowManager.Tests";

    [Fact]
    public async Task S15_5_first_run_registers_the_shortcut_and_Run_value_and_uninstall_removes_both()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var testKey = $@"{TestKeyRoot}\{Guid.NewGuid():N}";
        var runKey = testKey + @"\Run";
        var startMenu = Path.Combine(run.Root, "start-menu");
        run.ExtraEnvironment[StartMenuVariable] = startMenu;
        run.ExtraEnvironment[RunKeyVariable] = runKey;
        var settings = run.WriteDataFile("settings.json", new JsonObject
        {
            ["schemaVersion"] = 1,
            ["activePreset"] = "subzerodev.default",
            ["startAtSignIn"] = true,
        }.ToJsonString());

        try
        {
            run.Start(HostRun.GuardianExe, "--first-run");
            await run.WaitForAsync(s => (bool?)s["ready"] == true, TimeSpan.FromSeconds(30));

            var shortcut = Path.Combine(startMenu, ShortcutName);
            Assert.True(File.Exists(shortcut), $"No shortcut at {shortcut}.");
            Assert.True(NamesTarget(File.ReadAllBytes(shortcut), Path.GetFileName(HostRun.GuardianExe)), "The shortcut does not name the guardian.");
            using (var key = Registry.CurrentUser.OpenSubKey(runKey))
            {
                Assert.Equal($"\"{Path.GetFullPath(HostRun.GuardianExe)}\"", key?.GetValue(RunValueName) as string, ignoreCase: true);
            }

            Assert.Equal(0, await run.StopAsync());

            run.Start(HostRun.GuardianExe, "--uninstall");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await run.Process!.WaitForExitAsync(cancel.Token);
            Assert.Equal(0, run.Process.ExitCode);

            Assert.False(File.Exists(shortcut), "--uninstall left the shortcut.");
            using (var key = Registry.CurrentUser.OpenSubKey(runKey))
            {
                Assert.Null(key?.GetValue(RunValueName));
            }

            Assert.True(File.Exists(settings), "--uninstall touched the data folder.");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            using var root = Registry.CurrentUser.OpenSubKey(TestKeyRoot, writable: true);
            if (root is { SubKeyCount: 0, ValueCount: 0 })
            {
                root.Dispose();
                Registry.CurrentUser.DeleteSubKey(TestKeyRoot, throwOnMissingSubKey: false);
            }
        }
    }

    /// <summary>A shell link keeps its target path in the system code page, Unicode, or both.</summary>
    private static bool NamesTarget(byte[] link, string fileName) =>
        Contains(link, Encoding.Unicode.GetBytes(fileName)) || Contains(link, Encoding.ASCII.GetBytes(fileName));

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
