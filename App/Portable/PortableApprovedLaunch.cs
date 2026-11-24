// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MouseWithoutBorders.Core;

/// <summary>A per-user opt-in route to an already approved, fixed protected task.</summary>
internal static class PortableApprovedLaunch
{
    internal const string MainArgument = "--mwb-approved-main";
    internal const string SetupArgument = "--mwb-approved-setup";
    internal const string RegistryPath = @"Software\MouseWithoutBorders\Portable\ApprovedLaunch";
    private static string? originalExecutable;
    internal static bool IsActive => originalExecutable != null;
    internal static bool IsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64
        && typeof(PortableApprovedLaunch).Assembly.GetManifestResourceInfo("MouseWithoutBorders.ApprovedLauncher.exe") != null;
    internal static string PreferenceExecutablePath => originalExecutable ?? PortableApplication.CurrentExecutablePath;
    internal static string SettingsDirectory => Path.GetDirectoryName(PreferenceExecutablePath)!;

    internal static bool ShouldUseTask(string[]? args, bool enabled, bool elevated, bool system) =>
        args is { Length: 1 } && enabled && !elevated && !system;

    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key != null && PortableElevation.IsEnabled(key, PreferenceExecutablePath)
                && PortableApprovedTask.HasApproval(PreferenceExecutablePath);
        }
        catch (Exception) { return false; }
    }

    private static void SaveChoice(string executable, bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                ?? throw new IOException("The local approved-launch choice could not be saved.");
            PortableElevation.SetEnabled(key, executable, enabled: true);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key != null) PortableElevation.SetEnabled(key, executable, enabled: false);
        }
    }

    // Called before settings, input hooks, networking and ordinary self-elevation.
    // Returning true means this special invocation has finished and must exit.
    internal static bool HandleSpecialInvocation(string[] args)
    {
        if (args.Length < 2) return false;
        if (args[1] == MainArgument)
        {
            try
            {
                if (args.Length != 2 || SecureDesktopAgent.IsSystem || !PortableElevation.IsElevated()
                    || !SamePath(PortableApplication.CurrentExecutablePath,
                        Path.Combine(PortableApprovedTask.RootDirectory, "MouseWithoutBorders.exe")))
                    throw new InvalidOperationException();
                string source = PortableApprovedTask.ReadSourceExecutable();
                if (!PortableApprovedTask.IsConfigured(source)) throw new InvalidOperationException();
                originalExecutable = source;
                return false;
            }
            catch (Exception)
            {
                Environment.ExitCode = 1;
                return true;
            }
        }
        if (args[1] != SetupArgument) return false;
        Environment.ExitCode = 1;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!IsSupported || !IsSetupShape(args, identity.User?.Value) || SecureDesktopAgent.IsSystem
                || !PortableElevation.IsElevated()) return true;
            string source = SamePath(PortableApplication.CurrentExecutablePath,
                Path.Combine(PortableApprovedTask.RootDirectory, "MouseWithoutBorders.exe"))
                ? PortableApprovedTask.ReadSourceExecutable() : PortableApplication.CurrentExecutablePath;
            if (args[2] == "enable")
            {
                // Refuse a different user's credentials and an unconfigured copy.
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(source)!, PortableApplication.SettingsFileName)))
                    throw new IOException("Configure this installed copy before setting up approved launch.");
                PortableApprovedTask.Enable(source);
                try { SaveChoice(source, enabled: true); }
                catch { PortableApprovedTask.Disable(source); throw; }
            }
            else
            {
                PortableApprovedTask.Disable(source);
                SaveChoice(source, enabled: false);
            }
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            // The parent UI reports incomplete setup. A command-mode child must
            // not leave an unattended modal dialog or echo paths/preferences.
            Console.Error.WriteLine("ApprovedLaunchSetup " + error.GetType().Name + " 0x" + error.HResult.ToString("X8"));
        }
        return true;
    }

    internal static bool IsSetupShape(string[]? args, string? sid) => args is { Length: 4 }
        && args[1] == SetupArgument && args[2] is "enable" or "disable"
        && !string.IsNullOrWhiteSpace(sid) && string.Equals(args[3], sid, StringComparison.Ordinal);

    internal static ProcessStartInfo CreateSetupStartInfo(bool enabled, string sid)
    {
        // Always launch the trusted current image. A protected main must never
        // inherit its High token into a replacement from a user-writable folder.
        var info = new ProcessStartInfo(PortableApplication.CurrentExecutablePath)
        {
            UseShellExecute = true, Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(PortableApplication.CurrentExecutablePath),
        };
        info.ArgumentList.Add(SetupArgument);
        info.ArgumentList.Add(enabled ? "enable" : "disable");
        info.ArgumentList.Add(sid);
        return info;
    }

    internal static async Task<bool> ConfigureAsync(IWin32Window owner, bool enable)
    {
        string message = enable
            ? "Set up approved administrator launch for this Windows account?\r\n\r\n"
              + "A protected MWB copy and an elevated launch task will be created. "
              + "After setup, the whole MWB app, including remote input, can start as administrator without a new prompt. "
              + "Updates to the protected copy require approval."
            : "Remove this Windows account's approved launch task?\r\n\r\n"
              + "Future administrator launches will ask for Windows approval again. "
              + "The currently running copy keeps its permissions until you exit.";
        if (MessageBox.Show(owner, message, "Mouse Without Borders", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var child = Process.Start(CreateSetupStartInfo(enable, identity.User!.Value));
            if (child == null) return false;
            await child.WaitForExitAsync();
            bool complete = child.ExitCode == 0 && IsEnabled() == enable;
            if (!complete)
                MessageBox.Show(owner, "Approved launch setup did not complete. Use approval for the same Windows account. "
                    + "To update the protected copy, close it and reopen the ordinary installed copy first.",
                    "Mouse Without Borders", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return complete;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
        catch (Exception)
        {
            MessageBox.Show(owner, "Windows could not complete approved-launch setup. "
                + "Your ordinary launch settings were not changed.", "Mouse Without Borders",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
    }

    internal static bool TryRelaunch(string[] args)
    {
        if (args.Length != 1) return false;
        if (!ShouldUseTask(args, IsEnabled() && PortableElevation.IsEnabled(), PortableElevation.IsElevated(), SecureDesktopAgent.IsSystem)) return false;
        string source = PreferenceExecutablePath;
        try
        {
            // Opening another shortcut while MWB is already elevated should not
            // start a second connection/input owner or request another prompt.
            if (HasVerifiedMain(source)) return true;
            if (!PortableApprovedTask.TryRun(source)) throw new InvalidOperationException();
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 10000)
            {
                if (HasVerifiedMain(source)) return true;
                System.Threading.Thread.Sleep(100);
            }
        }
        catch (Exception) { }
        MessageBox.Show("The approved administrator copy did not start. "
            + "If MWB was updated, run approved-launch setup again from the ordinary installed copy. "
            + "Windows can request ordinary administrator approval instead.", "Mouse Without Borders",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private static bool HasVerifiedMain(string source)
    {
        string target = Path.Combine(PortableApprovedTask.RootDirectory, "MouseWithoutBorders.exe");
        using var identity = WindowsIdentity.GetCurrent();
        uint session;
        if (!Native.ProcessIdToSessionId((uint)Environment.ProcessId, out session)) return false;
        foreach (var process in Process.GetProcessesByName("MouseWithoutBorders"))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && IsVerifiedHighProcess(process.Id, target, source,
                    identity.User!.Value, session)) return true;
            }
        }
        return false;
    }

    private static bool IsVerifiedHighProcess(int id, string target, string source, string sid, uint session)
    {
        using var handle = Native.OpenProcess(0x101000, false, id); // query-limited + synchronize
        if (handle.IsInvalid || Native.WaitForSingleObject(handle, 0) != 258) return false;
        uint otherSession;
        if (!Native.ProcessIdToSessionId((uint)id, out otherSession) || otherSession != session) return false;
        var name = new StringBuilder(32768);
        int length = name.Capacity;
        if (!Native.QueryFullProcessImageName(handle, 0, name, ref length)
            || !(SamePath(name.ToString(), target) || SamePath(name.ToString(), source))) return false;
        if (!Native.OpenProcessToken(handle, 8, out var token)) return false;
        using (token)
        {
            return Native.TokenSid(token, 1) == sid && Native.TokenSid(token, 25) == "S-1-16-12288"
                && Native.TokenNumber(token, 20) == 1 && Native.TokenNumber(token, 26) == 0;
        }
    }

    private static bool SamePath(string first, string second) => Path.IsPathFullyQualified(first)
        && Path.IsPathFullyQualified(second)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint id, out uint session);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int count);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr data, int size, out int needed);
        private static IntPtr ReadToken(SafeAccessTokenHandle token, int kind)
        {
            GetTokenInformation(token, kind, IntPtr.Zero, 0, out int size);
            if (size <= 0 || size > 65536) throw new InvalidOperationException();
            var memory = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, kind, memory, size, out _))
            { Marshal.FreeHGlobal(memory); throw new InvalidOperationException(); }
            return memory;
        }
        internal static string TokenSid(SafeAccessTokenHandle token, int kind)
        {
            var data = ReadToken(token, kind);
            try { return new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value; }
            finally { Marshal.FreeHGlobal(data); }
        }
        internal static int TokenNumber(SafeAccessTokenHandle token, int kind)
        {
            var data = ReadToken(token, kind);
            try { return Marshal.ReadInt32(data); } finally { Marshal.FreeHGlobal(data); }
        }
    }
}
#endif
