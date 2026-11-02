using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Recovery;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// The host's side of the guardian: answers each <c>ping</c> on the guardian's pipe once the UI thread
/// has run (so a hung UI thread goes unanswered), and relaunches a guardian that dies with
/// <c>--attach &lt;hostPid&gt;</c>, within the restart budget.
/// </summary>
internal sealed class GuardianLink : IAsyncDisposable
{
    public const string GuardianExeVariable = "SUBZERODEV_WM_GUARDIAN_EXE";
    private const string GuardianExeName = "SubZeroDev.WindowManager.exe";

    private static readonly TimeSpan[] Backoffs = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly DispatcherQueue _dispatcher;
    private readonly IHostLog _log;
    private readonly RestartBudget _budget;
    private readonly string _guardianExe;
    private readonly bool _replacement;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _guardianPid;
    private string _pipeName;

    private GuardianLink(int guardianPid, string pipeName, string guardianExe, bool replacement, DispatcherQueue dispatcher, IHostLog log, RestartPolicy policy)
    {
        _guardianPid = guardianPid;
        _pipeName = pipeName;
        _guardianExe = guardianExe;
        _replacement = replacement;
        _dispatcher = dispatcher;
        _log = log;
        _budget = new RestartBudget(policy, Backoffs);
        _loop = Task.Run(RunAsync);
    }

    public int GuardianPid => Volatile.Read(ref _guardianPid);

    public string PipeName => Volatile.Read(ref _pipeName);

    /// <summary>Null when the host was started without a guardian.</summary>
    public static GuardianLink? Start(HostOptions options, DispatcherQueue dispatcher, IHostLog log, RestartPolicy policy)
    {
        if (options.GuardianPid is not { } pid || string.IsNullOrWhiteSpace(options.GuardianPipe))
        {
            return null;
        }

        return new GuardianLink(pid, options.GuardianPipe, FindGuardian(pid), options.Mode == HostMode.Replacement, dispatcher, log, policy);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await SuperviseAsync(_stop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write(LogLevel.Error, "guardian", $"The guardian link failed; running without a guardian: {e}");
        }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using (var watch = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var answering = AnswerPingsAsync(PipeName, watch.Token);
                await WaitForExitAsync(GuardianPid, ct);
                await watch.CancelAsync();
                await answering;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (_budget.RecordFault(DateTimeOffset.UtcNow) is not { } backoff)
            {
                _log.Write(LogLevel.Error, "guardian", "The guardian keeps dying; running without one.");
                return;
            }

            _log.Write(LogLevel.Warning, "guardian", $"Guardian {GuardianPid} exited; relaunching it in {backoff.TotalSeconds:0} s.");
            await Task.Delay(backoff, ct);
            try
            {
                var info = new ProcessStartInfo(_guardianExe) { UseShellExecute = false };
                info.ArgumentList.Add("--attach");
                info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                if (_replacement)
                {
                    info.ArgumentList.Add("--mode");
                    info.ArgumentList.Add("replacement");
                }

                using var guardian = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
                Volatile.Write(ref _pipeName, GuardianNames.AttachPipe(Environment.ProcessId));
                Volatile.Write(ref _guardianPid, guardian.Id);
                _log.Write(LogLevel.Info, "guardian", $"Guardian relaunched as {guardian.Id}.");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _log.Write(LogLevel.Error, "guardian", $"Could not relaunch the guardian {_guardianExe}: {e.Message}");
                Volatile.Write(ref _guardianPid, 0);
            }
        }
    }

    private static async Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        if (pid <= 0)
        {
            return;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Connects to the guardian's pipe (retrying until it exists) and answers each ping after a trip through the
    /// UI thread. A broken connection is reconnected, since the guardian serves the pipe again after a failure.
    /// </summary>
    private async Task AnswerPingsAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(TimeSpan.FromSeconds(2), ct);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line != GuardianNames.Ping)
                    {
                        continue;
                    }

                    var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_dispatcher.TryEnqueue(ran.SetResult))
                    {
                        return;
                    }

                    await ran.Task.WaitAsync(ct);
                    await writer.WriteLineAsync(GuardianNames.Pong.AsMemory(), ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log.Write(LogLevel.Warning, "guardian", $"Guardian pipe {pipeName} closed: {e.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The running guardian's executable, else the configured one, else the one next to the host folder.</summary>
    internal static string FindGuardian(int? pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid ?? -1);
            if (process.MainModule?.FileName is { } path)
            {
                return path;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        var configured = Environment.GetEnvironmentVariable(GuardianExeVariable);
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", GuardianExeName));
    }
}
