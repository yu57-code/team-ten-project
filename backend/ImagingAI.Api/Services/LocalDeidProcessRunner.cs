using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ImagingAI.Api.Services;

// Windows-only resume runner. JOB_LIST assigns the job atomically at process creation:
// even a host crash between CreateProcess and ResumeThread cannot leave a running orphan.
public static class LocalDeidProcessRunner
{
    public static async Task<int> RunAsync(string executable, string directory, IReadOnlyList<string> args,
        TimeSpan timeout, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Resume requires Windows process lifetime protection");
        token.ThrowIfCancellationRequested();
        var exe = Path.IsPathRooted(executable) ? executable : (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator).Select(p => Path.Combine(p, executable)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException();
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception();
        var security = new Security { Size = Marshal.SizeOf<Security>(), Inherit = true };
        using var nul = CreateFile("NUL", 0xc0000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (nul.IsInvalid) throw new Win32Exception();
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size); var jobPtr = Marshal.AllocHGlobal(IntPtr.Size); var handlePtr = Marshal.AllocHGlobal(IntPtr.Size);
        var initialized = false;
        var info = new ProcessInfo();
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception();
            initialized = true;
            Marshal.WriteIntPtr(jobPtr, job.DangerousGetHandle()); Marshal.WriteIntPtr(handlePtr, nul.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000d, jobPtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handlePtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupEx { Info = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>(), Flags = 0x100,
                Input = nul.DangerousGetHandle(), Output = nul.DangerousGetHandle(), Error = nul.DangerousGetHandle() }, Attributes = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { exe }.Concat(args).Select(Quote)));
            if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080004, IntPtr.Zero, directory, ref startup, out info)) throw new Win32Exception();
            using var process = Process.GetProcessById((int)info.ProcessId);
            token.ThrowIfCancellationRequested();
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); return process.ExitCode; }
            catch (OperationCanceledException)
            {
                if (!TerminateJobObject(job, 1)) throw new Win32Exception();
                while (true)
                {
                    if (!QueryInformationJobObject(job, 1, out var accounting,
                        (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero)) throw new Win32Exception();
                    if (accounting.ActiveProcesses == 0) break;
                    await Task.Delay(25, CancellationToken.None);
                }
                await process.WaitForExitAsync(CancellationToken.None);
                token.ThrowIfCancellationRequested(); throw new TimeoutException();
            }
        }
        finally
        {
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread);
            if (info.Process != IntPtr.Zero) CloseHandle(info.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(jobPtr); Marshal.FreeHGlobal(handlePtr);
        }
    }
    internal static string Quote(string value)
    {
        var output = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); output.Append(c); slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    { public Handle() : base(true) { } protected override bool ReleaseHandle() => CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(Handle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(Handle job, int kind, out JobAccounting accounting,
        uint size, IntPtr returned);
    [StructLayout(LayoutKind.Sequential)] private struct Security { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { public uint Size; public IntPtr Reserved, Desktop, Title; public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags; public ushort Show,Reserved2; public IntPtr ReservedPtr,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern Handle CreateJobObject(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(Handle job,int kind,ref JobLimits info,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string name,uint access,uint share,ref Security security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcess(string app,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo info);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
