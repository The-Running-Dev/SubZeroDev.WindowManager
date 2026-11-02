using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>Exit codes shared with the guardian.</summary>
public static class ExitCodes
{
    public const int Normal = 0;
    public const int AlreadyRunning = 2;
    public const int StartupFailed = 10;
}

/// <summary>The arguments the guardian passes: <c>--guardian-pid &lt;pid&gt; --guardian-pipe &lt;name&gt;</c>.</summary>
public sealed record HostOptions(IReadOnlyList<string> Args, int? GuardianPid, string? GuardianPipe)
{
    public static HostOptions Parse(string[] args)
    {
        int? pid = null;
        string? pipe = null;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--guardian-pid" when int.TryParse(args[i + 1], out var value):
                    pid = value;
                    i++;
                    break;
                case "--guardian-pipe":
                    pipe = args[++i];
                    break;
            }
        }

        return new HostOptions(args, pid, pipe);
    }
}

public static class Program
{
    public const string InstanceVariable = "SUBZERODEV_WM_INSTANCE";

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var options = HostOptions.Parse(args);
        var suffix = Environment.GetEnvironmentVariable(InstanceVariable) ?? string.Empty;
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SubZeroDev.WindowManager.Host" + suffix, out var created);
        if (!created)
        {
            return ExitCodes.AlreadyRunning;
        }

        // Before any worker starts: a release without a machine-wide .NET runs plugins on its own runtime.
        Core.Plugins.BundledRuntime.Apply();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(options);
        });
        return App.ExitCode;
    }
}
