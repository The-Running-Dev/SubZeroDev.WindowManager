using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Recovery;

namespace SubZeroDev.WindowManager.Guardian;

/// <summary>
/// The process the user starts. It recovers a stale journal, starts the host, pings it on a private pipe,
/// kills it when it stops answering for 30 s, reverts its desktop changes when it dies, and restarts it
/// within the restart budget. <c>--attach &lt;hostPid&gt;</c> watches an already running host (the host
/// relaunches a guardian that died); <c>--recover-only</c> runs the stale-journal procedure and exits.
/// </summary>
public static partial class Program
{
    public const int Normal = 0;
    public const int AlreadyRunning = 2;
    public const int BudgetSpent = 3;
    public const int HostNotStarted = 4;
    public const string HostExeVariable = "SUBZERODEV_WM_HOST_EXE";
    public const string InstanceVariable = "SUBZERODEV_WM_INSTANCE";
    public const string DataRootVariable = "SUBZERODEV_WM_DATA_ROOT";
    public const string TestHookVariable = "SUBZERODEV_WM_TEST_HOOK";
    public const string HostExeName = "SubZeroDev.WindowManager.Host.exe";

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoffs = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> Main(string[] args)
    {
        var suffix = Environment.GetEnvironmentVariable(InstanceVariable) ?? string.Empty;
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SubZeroDev.WindowManager.Guardian" + suffix, out var created);
        if (!created)
        {
            return AlreadyRunning;
        }

        var guardian = new Guardian(args);
        try
        {
            return await guardian.RunAsync();
        }
        finally
        {
            guardian.Log("Guardian exiting.");
        }
    }

    private sealed class Guardian
    {
        private readonly string[] _args;
        private readonly string _dataRoot;
        private readonly string _logPath;
        private readonly string? _hookDirectory;
        private readonly int _sessionId;
        private readonly Lock _logGate = new();
        private string _state = "starting";
        private int? _hostPid;

        public Guardian(string[] args)
        {
            _args = args;
            var configured = Environment.GetEnvironmentVariable(DataRootVariable);
            _dataRoot = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubZeroDev", "WindowManager");
            _logPath = Path.Combine(_dataRoot, "logs", $"guardian-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var hook = Environment.GetEnvironmentVariable(TestHookVariable);
            _hookDirectory = string.IsNullOrWhiteSpace(hook) ? null : hook;
            using var self = Process.GetCurrentProcess();
            _sessionId = self.SessionId;
        }

        public async Task<int> RunAsync()
        {
            Log($"Guardian {Environment.ProcessId} started: {string.Join(' ', _args)}");
            if (_args.Contains("--recover-only"))
            {
                SetState("recovering");
                Recover("staleJournal", hostExited: false, exitCode: null);
                return Normal;
            }

            Process? host = null;
            string pipeName;
            var attachIndex = Array.IndexOf(_args, "--attach");
            if (attachIndex >= 0)
            {
                if (attachIndex + 1 >= _args.Length || !int.TryParse(_args[attachIndex + 1], CultureInfo.InvariantCulture, out var hostPid))
                {
                    Log("--attach needs the host's process id.");
                    return HostNotStarted;
                }

                try
                {
                    host = Process.GetProcessById(hostPid);
                    _ = host.Handle;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Log($"Host {hostPid} to attach to is not running: {e.Message}");
                    host?.Dispose();
                    host = null;
                }

                pipeName = GuardianNames.AttachPipe(hostPid);
                if (host is null)
                {
                    SetState("recovering");
                    Recover("staleJournal", hostExited: false, exitCode: null);
                }
            }
            else
            {
                SetState("recovering");
                Recover("staleJournal", hostExited: false, exitCode: null);
                pipeName = GuardianNames.NewPipe();
            }

            var budget = new Budget(LoadPolicy());
            while (true)
            {
                if (host is null)
                {
                    host = StartHost(pipeName);
                    if (host is null)
                    {
                        return HostNotStarted;
                    }
                }

                _hostPid = host.Id;
                SetState("watching");
                var hung = await WatchAsync(host, pipeName);
                int exitCode;
                using (host)
                {
                    exitCode = host.ExitCode;
                }

                host = null;
                if (!hung && exitCode == Normal)
                {
                    // The user chose to exit; whatever the host left journalled is reverted, but it is not restarted.
                    if (!CleanJournal())
                    {
                        Log($"Host {_hostPid} exited normally but left desktop changes journalled; restoring the desktop.");
                        Recover("hostExited", hostExited: true, exitCode);
                    }

                    Log("Host exited normally.");
                    return Normal;
                }

                if (!hung && exitCode == AlreadyRunning)
                {
                    Log("Another host is already running.");
                    return AlreadyRunning;
                }

                var reason = hung ? "hostHung" : "hostExited";
                Log($"Host {_hostPid} ended ({reason}, exit code {exitCode}); restoring the desktop.");
                SetState("recovering");
                Recover(reason, hostExited: true, exitCode);

                if (budget.RecordFault(DateTimeOffset.UtcNow) is not { } backoff)
                {
                    Log("The host keeps failing; restart budget spent.");
                    await GiveUpAsync();
                    return BudgetSpent;
                }

                SetState("restarting");
                await Task.Delay(backoff);
                pipeName = GuardianNames.NewPipe();
            }
        }

        /// <summary>
        /// Pings the host every 5 s; returns true when it went 30 s without answering and was killed. Silence is
        /// counted a tick (at most 2 s) at a time, so a machine waking from sleep does not count the sleep as a hang.
        /// </summary>
        private async Task<bool> WatchAsync(Process host, string pipeName)
        {
            using var stop = new CancellationTokenSource();
            long replies = 0;
            var pinging = PingAsync(pipeName, () => Interlocked.Increment(ref replies), stop.Token);
            var hung = false;
            var seen = 0L;
            var silent = TimeSpan.Zero;
            var clock = Stopwatch.StartNew();
            while (!host.HasExited)
            {
                using var tick = new CancellationTokenSource(Tick);
                try
                {
                    await host.WaitForExitAsync(tick.Token);
                }
                catch (OperationCanceledException)
                {
                }

                var elapsed = clock.Elapsed;
                clock.Restart();
                var answered = Interlocked.Read(ref replies);
                if (answered != seen)
                {
                    seen = answered;
                    silent = TimeSpan.Zero;
                    continue;
                }

                silent += elapsed < Tick * 2 ? elapsed : Tick * 2;
                if (!host.HasExited && silent > HangLimit)
                {
                    Log($"Host {host.Id} has not answered for {HangLimit.TotalSeconds:0} s; killing it.");
                    hung = true;
                    try
                    {
                        host.Kill(entireProcessTree: true);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        Log($"Could not kill host {host.Id}: {e.Message}");
                    }

                    using var wait = new CancellationTokenSource(KillWait);
                    try
                    {
                        await host.WaitForExitAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Log($"Host {host.Id} is still running {KillWait.TotalSeconds:0} s after being killed; trying again.");
                    }
                }
            }

            await stop.CancelAsync();
            await pinging;
            return hung;
        }

        /// <summary>Serves the pipe, one host connection at a time, and writes a ping every 5 s.</summary>
        private async Task PingAsync(string pipeName, Action replied, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(ct);
                    Log($"Host connected to {pipeName}.");
                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, Utf8NoBom, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                    var reading = ReadRepliesAsync(reader, replied, ct);
                    while (!ct.IsCancellationRequested && !reading.IsCompleted)
                    {
                        await writer.WriteLineAsync(GuardianNames.Ping.AsMemory(), ct);
                        await Task.WhenAny(reading, Task.Delay(PingInterval, ct));
                    }

                    await reading;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Any failure here would otherwise stop the pings and get a healthy host killed as hung.
                    Log($"Pipe {pipeName}: {e.GetType().Name}: {e.Message}");
                    try
                    {
                        await Task.Delay(Tick, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task ReadRepliesAsync(StreamReader reader, Action replied, CancellationToken ct)
        {
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line == GuardianNames.Pong)
                    {
                        replied();
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
            }
        }

        private Process? StartHost(string pipeName)
        {
            var hostExe = FindHost();
            if (hostExe is null)
            {
                Log("The host executable was not found.");
                return null;
            }

            var start = new ProcessStartInfo(hostExe) { UseShellExecute = false };
            start.ArgumentList.Add("--guardian-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--guardian-pipe");
            start.ArgumentList.Add(pipeName);
            try
            {
                var host = Process.Start(start);
                if (host is not null)
                {
                    Log($"Started host {host.Id} on {pipeName}.");
                }

                return host;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Log($"Could not start the host {hostExe}: {e.Message}");
                return null;
            }
        }

        /// <summary>True when the host marked a clean exit and left nothing journalled.</summary>
        private bool CleanJournal()
        {
            var read = Journal.Read(Journal.PathForSession(_dataRoot, _sessionId));
            return read.Value is { CleanExit: true, Entries.Count: 0 };
        }

        private void Recover(string reason, bool hostExited, int? exitCode) =>
            Recoverer.Recover(_dataRoot, _sessionId, reason, hostExited, exitCode, Log);

        /// <summary>Tells the user and waits for them; under the test hook a <c>dismiss</c> file stands in for the message box.</summary>
        private async Task GiveUpAsync()
        {
            SetState("idle");
            if (_hookDirectory is not null)
            {
                var dismiss = Path.Combine(_hookDirectory, "dismiss");
                while (!File.Exists(dismiss))
                {
                    await Task.Delay(100);
                }

                return;
            }

            MessageBoxW(0, $"SubZeroDev.WindowManager stopped after repeated failures. The desktop has been restored.\n\nDetails: {_logPath}",
                "SubZeroDev.WindowManager", MbOk | MbIconError);
        }

        private RestartLimits LoadPolicy()
        {
            try
            {
                var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(_dataRoot, "settings.json")));
                if (settings?["restartPolicy"] is JsonObject policy
                    && policy["maxRestarts"] is JsonValue max && max.TryGetValue<int>(out var maxRestarts) && maxRestarts >= 0
                    && policy["windowSeconds"] is JsonValue window && window.TryGetValue<int>(out var windowSeconds) && windowSeconds > 0)
                {
                    return new RestartLimits(maxRestarts, TimeSpan.FromSeconds(windowSeconds));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            return RestartLimits.Default;
        }

        private static string? FindHost()
        {
            var configured = Environment.GetEnvironmentVariable(HostExeVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return File.Exists(configured) ? configured : null;
            }

            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "host", HostExeName),
                Path.Combine(AppContext.BaseDirectory, HostExeName),
            ];
            return candidates.FirstOrDefault(File.Exists);
        }

        public void Log(string message)
        {
            lock (_logGate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    File.AppendAllText(_logPath,
                        $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [guardian] {message}\n", Utf8NoBom);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Under the test hook, publishes what the guardian is doing to <c>guardian.json</c>.</summary>
        private void SetState(string state)
        {
            _state = state;
            if (_hookDirectory is null)
            {
                return;
            }

            var status = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["args"] = new JsonArray(_args.Select(a => (JsonNode?)a).ToArray()),
                ["state"] = _state,
                ["hostPid"] = _hostPid,
                ["log"] = _logPath,
            };
            try
            {
                Directory.CreateDirectory(_hookDirectory);
                var temp = Path.Combine(_hookDirectory, "guardian.json.tmp");
                File.WriteAllText(temp, status.ToJsonString(), Utf8NoBom);
                File.Move(temp, Path.Combine(_hookDirectory, "guardian.json"), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record RestartLimits(int MaxRestarts, TimeSpan Window)
    {
        public static readonly RestartLimits Default = new(3, TimeSpan.FromSeconds(300));
    }

    /// <summary>A sliding window of host failures: the fault that takes the count past the limit spends the budget.</summary>
    private sealed class Budget(RestartLimits limits)
    {
        private readonly Queue<DateTimeOffset> _faults = new();

        public TimeSpan? RecordFault(DateTimeOffset now)
        {
            _faults.Enqueue(now);
            while (_faults.Count > 0 && now - _faults.Peek() > limits.Window)
            {
                _faults.Dequeue();
            }

            return _faults.Count > limits.MaxRestarts ? null : Backoffs[Math.Min(_faults.Count, Backoffs.Length) - 1];
        }
    }

    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);
}
