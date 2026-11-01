using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Starts plugin workers inside one job object that kills them when the host's handle closes, so a
/// worker never outlives the host however the host ends (I3). A worker is created suspended, put in
/// the job, then resumed, so it cannot start a child outside the job. It inherits only its stderr pipe
/// and the NUL device; nothing else of the host's.
/// </summary>
public sealed unsafe class WorkerLauncher : IWorkerLauncher, IDisposable
{
    private readonly SafeFileHandle _job;

    public WorkerLauncher()
    {
        _job = JobNative.CreateJobObject(0, null);
        if (_job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var limits = new JobExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobNative.JobObjectLimitKillOnJobClose } };
        if (!JobNative.SetInformationJobObject(_job, JobNative.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobExtendedLimitInformation)))
        {
            var error = Marshal.GetLastPInvokeError();
            _job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    public Task<Outcome<IWorkerProcess>> StartAsync(WorkerStartInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Outcome<IWorkerProcess>.Success(Start(info)));
        }
        catch (Win32Exception e)
        {
            return Task.FromResult(Outcome<IWorkerProcess>.Fail(CoreErrorCode.IoFailure, $"{info.ExecutablePath}: {e.Message}"));
        }
    }

    public IReadOnlyList<int> JobProcessIds()
    {
        const int capacity = 512;
        var size = sizeof(uint) * 2 + sizeof(nuint) * capacity;
        var buffer = stackalloc byte[size];
        if (!JobNative.QueryInformationJobObject(_job, JobNative.JobObjectBasicProcessIdList, buffer, (uint)size, out _))
        {
            return [];
        }

        var count = ((uint*)buffer)[1];
        var ids = (nuint*)(buffer + sizeof(uint) * 2);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = (int)ids[i];
        }

        return result;
    }

    public void Dispose() => _job.Dispose();

    /// <summary>Quotes arguments so the worker's <c>CommandLineToArgvW</c> reads them back unchanged.</summary>
    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Append(line, executable);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Append(line, argument);
        }

        return line.ToString();

        static void Append(StringBuilder line, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                return;
            }

            line.Append('"');
            var backslashes = 0;
            foreach (var c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                line.Append(c);
            }

            line.Append('\\', backslashes * 2);
            line.Append('"');
        }
    }

    private WorkerProcess Start(WorkerStartInfo info)
    {
        using var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        if (!JobNative.SetHandleInformation(nul, JobNative.HandleFlagInherit, JobNative.HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed.");
        }

        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            var handles = stackalloc nint[] { nul.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle() };
            nuint listSize = 0;
            JobNative.InitializeProcThreadAttributeList(null, 1, 0, ref listSize);
            var list = (byte*)NativeMemory.Alloc(listSize);
            try
            {
                if (!JobNative.InitializeProcThreadAttributeList(list, 1, 0, ref listSize))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
                }

                try
                {
                    if (!JobNative.UpdateProcThreadAttribute(list, 0, JobNative.ProcThreadAttributeHandleList, handles,
                            (nuint)(sizeof(nint) * 2), null, null))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed.");
                    }

                    var startup = new StartupInfoEx
                    {
                        StartupInfo =
                        {
                            Size = (uint)sizeof(StartupInfoEx),
                            Flags = JobNative.StartfUseStdHandles,
                            StdInput = nul.DangerousGetHandle(),
                            StdOutput = nul.DangerousGetHandle(),
                            StdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
                        },
                        AttributeList = list,
                    };
                    var commandLine = (BuildCommandLine(info.ExecutablePath, info.Arguments) + '\0').ToCharArray();
                    ProcessInformation process;
                    fixed (char* application = info.ExecutablePath)
                    fixed (char* command = commandLine)
                    fixed (char* directory = info.WorkingDirectory)
                    {
                        if (!JobNative.CreateProcess(application, command, null, null, true,
                                JobNative.CreateSuspended | JobNative.CreateNoWindow | JobNative.ExtendedStartupInfoPresent | JobNative.CreateUnicodeEnvironment,
                                null, directory, &startup, &process))
                        {
                            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed.");
                        }
                    }

                    var processHandle = new SafeProcessHandle(process.Process, ownsHandle: true);
                    using var threadHandle = new SafeProcessHandle(process.Thread, ownsHandle: true);
                    if (!JobNative.AssignProcessToJobObject(_job, processHandle))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        JobNative.TerminateProcess(processHandle, 1);
                        processHandle.Dispose();
                        throw new Win32Exception(error, "AssignProcessToJobObject failed.");
                    }

                    JobNative.ResumeThread(threadHandle);
                    stderr.DisposeLocalCopyOfClientHandle();
                    return new WorkerProcess(processHandle, (int)process.ProcessId, stderr);
                }
                finally
                {
                    JobNative.DeleteProcThreadAttributeList(list);
                }
            }
            finally
            {
                NativeMemory.Free(list);
            }
        }
        catch
        {
            stderr.Dispose();
            throw;
        }
    }

    private sealed class WorkerProcess : IWorkerProcess
    {
        private readonly SafeProcessHandle _handle;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RegisteredWaitHandle _wait;
        private readonly ProcessWaitHandle _waitHandle;

        public WorkerProcess(SafeProcessHandle handle, int processId, Stream standardError)
        {
            _handle = handle;
            ProcessId = processId;
            StandardError = standardError;
            _waitHandle = new ProcessWaitHandle(handle);
            _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) =>
            {
                // The wait can fire while Dispose closes the handle; an escaped exception here would end the host.
                try
                {
                    _exited.TrySetResult(JobNative.GetExitCodeProcess(_handle, out var code) ? (int)code : -1);
                }
                catch (ObjectDisposedException)
                {
                    _exited.TrySetResult(-1);
                }
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public int ProcessId { get; }

        public Stream StandardError { get; }

        public Task<int> Exited => _exited.Task;

        public void Kill() => JobNative.TerminateProcess(_handle, 1);

        public void Dispose()
        {
            _wait.Unregister(null);
            _waitHandle.Dispose();
            StandardError.Dispose();
            _handle.Dispose();
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobExtendedLimitInformation
{
    public JobBasicLimitInformation BasicLimitInformation;
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StartupInfo
{
    public uint Size;
    public char* Reserved;
    public char* Desktop;
    public char* Title;
    public uint X;
    public uint Y;
    public uint XSize;
    public uint YSize;
    public uint XCountChars;
    public uint YCountChars;
    public uint FillAttribute;
    public uint Flags;
    public ushort ShowWindow;
    public ushort Reserved2Size;
    public byte* Reserved2;
    public nint StdInput;
    public nint StdOutput;
    public nint StdError;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StartupInfoEx
{
    public StartupInfo StartupInfo;
    public byte* AttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public nint Process;
    public nint Thread;
    public uint ProcessId;
    public uint ThreadId;
}

internal static unsafe partial class JobNative
{
    public const int JobObjectBasicProcessIdList = 3;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JobObjectLimitKillOnJobClose = 0x2000;
    public const uint HandleFlagInherit = 0x1;
    public const uint StartfUseStdHandles = 0x100;
    public const uint CreateSuspended = 0x4;
    public const uint CreateUnicodeEnvironment = 0x400;
    public const uint ExtendedStartupInfoPresent = 0x80000;
    public const uint CreateNoWindow = 0x08000000;
    public const nuint ProcThreadAttributeHandleList = 0x20002;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(void* list, int count, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(void* list, uint flags, nuint attribute, void* value, nuint size, void* previous, nuint* returnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(void* list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(char* application, char* commandLine, void* processAttributes, void* threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, void* environment, char* directory,
        StartupInfoEx* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(SafeProcessHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
