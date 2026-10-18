// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Follows the Microsoft PowerToys MWB service's CreateProcessAsUser/desktop
// architecture, but duplicates this service's own SYSTEM token instead of opening
// another process, and never starts a SYSTEM instance on the ordinary desktop.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MouseWithoutBorders.SecureDesktop;

internal static class NativeSession
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref uint session, uint length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges, uint bufferLength, IntPtr previous, IntPtr returnLength);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation information);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessUserTimeLimit, JobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    internal sealed class OwnedAgent : IDisposable
    {
        private readonly SafeFileHandle job;
        private readonly SafeProcessHandle process;

        internal OwnedAgent(SafeFileHandle job, SafeProcessHandle process, uint sessionId)
        {
            this.job = job;
            this.process = process;
            SessionId = sessionId;
        }

        public uint SessionId { get; }
        public bool HasExited => WaitForSingleObject(process, 0) != 0x00000102;

        public void Dispose()
        {
            // Closing this exclusively owned job kills only this service's agent.
            // No process-name scans, PID-based kills or unrelated application stops.
            job.Dispose();
            _ = WaitForSingleObject(process, 3000);
            process.Dispose();
        }
    }

    internal static OwnedAgent Launch(uint sessionId)
    {
        ServiceTrust.RequireLocalSystem();
        if (!ServiceTrust.IsInteractiveSession(sessionId) || sessionId != WTSGetActiveConsoleSessionId())
        {
            throw new SecurityException("No active physical console session is available.");
        }

        ServiceTrust.ValidateInstallation();
        string extractionDirectory = Path.Combine(ServiceTrust.ProfileDirectory, "runtime");
        Directory.CreateDirectory(extractionDirectory);
        ServiceTrust.ValidatePrivateRuntimeDirectory(extractionDirectory);

        using Process self = Process.GetCurrentProcess();
        if (!OpenProcessToken(self.Handle, TokenDuplicate | TokenQuery | TokenAdjustPrivileges, out SafeAccessTokenHandle serviceToken))
        {
            throw new Win32Exception();
        }

        using (serviceToken)
        {
            EnableSessionPrivilege(serviceToken);
            uint access = TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId;
            if (!DuplicateTokenEx(serviceToken, access, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out SafeAccessTokenHandle primaryToken))
            {
                throw new Win32Exception();
            }

            using (primaryToken)
            {
                if (!SetTokenInformation(primaryToken, 12 /* TokenSessionId */, ref sessionId, sizeof(uint)))
                {
                    throw new Win32Exception();
                }

                return LaunchOnWinlogon(primaryToken, sessionId, extractionDirectory);
            }
        }
    }

    private static void EnableSessionPrivilege(SafeAccessTokenHandle token)
    {
        if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out Luid privilege)) throw new Win32Exception();
        var privileges = new TokenPrivileges { Count = 1, Luid = privilege, Attributes = 0x00000002 };
        if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
        int error = Marshal.GetLastWin32Error();
        if (error != 0) throw new Win32Exception(error);
    }

    private static OwnedAgent LaunchOnWinlogon(SafeAccessTokenHandle token, uint sessionId, string extractionDirectory)
    {
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
        SafeProcessHandle? process = null;
        IntPtr environment = IntPtr.Zero;
        try
        {
            var limits = new ExtendedLimitInformation
            {
                Basic = new BasicLimitInformation { LimitFlags = 0x00002000 | 0x00000008 /* kill-on-close, one process */, ActiveProcessLimit = 1 },
            };
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())) throw new Win32Exception();
            environment = CreateProtectedEnvironment(token, extractionDirectory);
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\winlogon" };

            // Never use shell execution or a command supplied by an ordinary user.
            // Start suspended so no agent code can run before job containment.
            if (!CreateProcessAsUser(token, ServiceTrust.AgentPath, new StringBuilder(ServiceTrust.AgentCommandLine), IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow, environment, ServiceTrust.InstallDirectory, ref startup, out ProcessInformation info))
            {
                throw new Win32Exception();
            }

            process = new SafeProcessHandle(info.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, process)) throw new Win32Exception();
            if (sessionId != WTSGetActiveConsoleSessionId()) throw new SecurityException("The console session changed during agent launch.");
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            return new OwnedAgent(job, process, sessionId);
        }
        catch
        {
            if (process != null) { _ = TerminateProcess(process, 1); process.Dispose(); }
            job.Dispose();
            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    private static IntPtr CreateProtectedEnvironment(SafeAccessTokenHandle token, string extractionDirectory)
    {
        if (!CreateEnvironmentBlock(out IntPtr original, token, false)) throw new Win32Exception();
        try
        {
            var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr cursor = original;
            while (true)
            {
                string entry = Marshal.PtrToStringUni(cursor) ?? string.Empty;
                if (entry.Length == 0) break;
                int separator = entry.IndexOf('=', 1);
                if (separator > 0) entries[entry[..separator]] = entry[(separator + 1)..];
                cursor += (entry.Length + 1) * sizeof(char);
            }

            entries["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extractionDirectory;
            entries["TEMP"] = extractionDirectory;
            entries["TMP"] = extractionDirectory;
            string block = string.Join('\0', entries.Select(item => item.Key + "=" + item.Value)) + "\0\0";
            return Marshal.StringToHGlobalUni(block);
        }
        finally
        {
            _ = DestroyEnvironmentBlock(original);
        }
    }
}
