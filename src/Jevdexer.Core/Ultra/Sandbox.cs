using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Jevdexer.Core.Ultra;

/// <summary>
/// Starts a process inside a Windows AppContainer with no capabilities: no network, no
/// access to the user's files, registry or other processes, in a job that dies with us and has a
/// memory ceiling. User mode, no admin. The child talks to us over its stdin and stdout only.
///
/// An AppContainer can read only what grants "ALL APPLICATION PACKAGES" (S-1-15-2-1) or its own
/// SID. Windows and Program Files (the .NET runtime) already do; the worker's own folder is ours,
/// so <see cref="Start"/> grants read and execute there once.
/// </summary>
public sealed partial class Sandbox : IDisposable
{
    public const string ContainerName = "Jevdexer.ContentWorker";
    private const string AllAppPackages = "S-1-15-2-1";

    public Stream Input { get; }
    public Stream Output { get; }
    public int ProcessId { get; }

    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _job;

    private Sandbox(SafeProcessHandle process, SafeFileHandle job, int pid, Stream input, Stream output)
    {
        _process = process;
        _job = job;
        ProcessId = pid;
        Input = input;
        Output = output;
    }

    /// <param name="exe">The worker executable.</param>
    /// <param name="args">Command line arguments (already quoted).</param>
    /// <param name="memoryLimitMb">The job's per-process memory ceiling.</param>
    public static Sandbox Start(string exe, string args, int memoryLimitMb = 1536)
    {
        GrantFolder(Path.GetDirectoryName(Path.GetFullPath(exe))!);
        var sid = ContainerSid();
        try
        {
            return Launch(exe, args, sid, memoryLimitMb);
        }
        finally
        {
            FreeSid(sid);
        }
    }

    public bool WaitForExit(TimeSpan timeout) =>
        WaitForSingleObject(_process, (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1)) == 0;

    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
        _job.Dispose(); // KILL_ON_JOB_CLOSE: the worker ends with its job
        _process.Dispose();
    }

    // ---------------------------------------------------------------- setup

    /// <summary>Lets AppContainers read and run the worker's folder. Only ever our own install or build folder.</summary>
    private static void GrantFolder(string dir)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        var who = new SecurityIdentifier(AllAppPackages);
        var granted = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(r => r.IdentityReference == who && r.AccessControlType == AccessControlType.Allow
                      && r.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
        if (granted) return;
        acl.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static IntPtr ContainerSid()
    {
        var hr = CreateAppContainerProfile(ContainerName, "Jevdexer content worker", "Reads file contents for Jevdexer, with no network or file access.",
            IntPtr.Zero, 0, out var sid);
        if (hr == unchecked((int)0x800700B7)) // already exists
            hr = DeriveAppContainerSidFromAppContainerName(ContainerName, out sid);
        if (hr != 0) throw new Win32Exception(hr, "AppContainer profile");
        return sid;
    }

    /// <summary>Removes the AppContainer profile (uninstall).</summary>
    public static void DeleteProfile() => DeleteAppContainerProfile(ContainerName);

    private static unsafe Sandbox Launch(string exe, string args, IntPtr sid, int memoryLimitMb)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        if (!CreatePipe(out var childIn, out var parentIn, ref sa, 0)) throw new Win32Exception();
        if (!CreatePipe(out var parentOut, out var childOut, ref sa, 0)) throw new Win32Exception();
        // Our ends must not be inherited.
        SetHandleInformation(parentIn, 1, 0);
        SetHandleInformation(parentOut, 1, 0);

        var caps = new SECURITY_CAPABILITIES { AppContainerSid = sid, Capabilities = IntPtr.Zero, CapabilityCount = 0 };
        var inherit = stackalloc IntPtr[2] { childIn.DangerousGetHandle(), childOut.DangerousGetHandle() };

        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        var attrs = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 2, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, ProcThreadAttributeSecurityCapabilities, (IntPtr)(&caps), (nuint)sizeof(SECURITY_CAPABILITIES), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, ProcThreadAttributeHandleList, (IntPtr)inherit, (nuint)(2 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = sizeof(STARTUPINFOEX);
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = childIn.DangerousGetHandle();
            si.StartupInfo.hStdOutput = childOut.DangerousGetHandle();
            si.StartupInfo.hStdError = childOut.DangerousGetHandle();
            si.lpAttributeList = attrs;

            var job = CreateJob(memoryLimitMb); // before the process, so a failure leaves nothing running
            var commandLine = $"\"{exe}\" {args}";
            if (!CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    ExtendedStartupInfoPresent | CreateNoWindow | CreateSuspended | BelowNormalPriority,
                    IntPtr.Zero, null, ref si, out var pi))
                throw new Win32Exception();

            var process = new SafeProcessHandle(pi.hProcess, true);
            if (!AssignProcessToJobObject(job, process))
            {
                var error = new Win32Exception();
                TerminateProcess(process, 1);
                throw error;
            }
            ResumeThread(pi.hThread);
            CloseHandle(pi.hThread);

            childIn.Dispose();
            childOut.Dispose();
            return new Sandbox(process, job, pi.dwProcessId,
                new FileStream(parentIn, FileAccess.Write, 1 << 16), new FileStream(parentOut, FileAccess.Read, 1 << 16));
        }
        finally
        {
            DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
        }
    }

    private static unsafe SafeFileHandle CreateJob(int memoryLimitMb)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobKillOnClose | JobProcessMemory | JobDieOnUnhandledException | JobActiveProcess;
        info.BasicLimitInformation.ActiveProcessLimit = 1; // the worker cannot start other processes
        info.ProcessMemoryLimit = (nuint)memoryLimitMb * 1024 * 1024;
        if (!SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, (IntPtr)(&info), (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
            throw new Win32Exception();
        return job;
    }

    // ---------------------------------------------------------------- Win32

    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateNoWindow = 0x08000000, CreateSuspended = 0x4, BelowNormalPriority = 0x4000;
    private const int StartfUseStdHandles = 0x100;
    private const int ProcThreadAttributeSecurityCapabilities = 0x20009, ProcThreadAttributeHandleList = 0x20002;
    private const uint JobKillOnClose = 0x2000, JobProcessMemory = 0x100, JobDieOnUnhandledException = 0x400, JobActiveProcess = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, uint count, out IntPtr sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeleteAppContainerProfile(string name);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr FreeSid(IntPtr sid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES sa, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string app, string commandLine, IntPtr processAttrs, IntPtr threadAttrs, bool inherit,
        uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(IntPtr attrs, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint code);

    [LibraryImport("kernel32.dll")]
    private static partial uint ResumeThread(IntPtr thread);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint ms);
}
