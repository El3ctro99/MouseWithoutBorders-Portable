// Copyright (c) Microsoft Corporation
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.PowerToys.Settings.UI.Library;
using MouseWithoutBorders.SecureDesktop;

namespace MouseWithoutBorders.Core;

/// <summary>Fail-closed startup boundary for the optional, input-only SYSTEM agent.</summary>
internal static class SecureDesktopAgent
{
    internal const string ExportArgument = "--export-secure-desktop-profile";
    private static uint validatedSessionId;
    internal static bool IsActive { get; private set; }
    internal static MouseWithoutBordersSettings InputSettings { get; private set; }

    internal static bool IsSystem
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
    }

    internal static bool IsAgentInvocation(string[] args) => args.Length > 1
        && string.Equals(args[1], ServiceTrust.AgentArgument, StringComparison.Ordinal);

    internal static bool IsLegacyServiceInvocation(string[] args) => args.Length > 1
        && (string.Equals(args[1], "winlogon", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[1], "default", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[1], "UseService", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[1], "Screen-saver", StringComparison.OrdinalIgnoreCase));

    internal static bool TryInitialize(string[] args)
    {
        try
        {
            if (!IsAgentInvocation(args) || args.Length != 2)
                throw new SecurityException("The secure desktop agent does not accept additional arguments.");
            ServiceTrust.RequireLocalSystem();
            using var process = Process.GetCurrentProcess();
            uint sessionId = (uint)process.SessionId;
            if (!IsValidContext(IsSystem, sessionId, WTSGetActiveConsoleSessionId(),
                ObjectName(GetProcessWindowStation()), ObjectName(GetThreadDesktop(GetCurrentThreadId())),
                Environment.ProcessPath, ServiceTrust.AgentPath))
                throw new SecurityException("The secure desktop agent is outside its protected execution context.");
            ServiceTrust.ValidateInstallation(requireServiceExecutable: false);
            var file = new FileInfo(ServiceTrust.ProfilePath);
            if (file.Length > SecureDesktopProfile.MaximumBytes)
                throw new InvalidDataException("The secure desktop profile exceeds its size limit.");
            // Never read adjacent portable preferences or accept a user-supplied path.
            var profile = SecureDesktopProfile.Parse(File.ReadAllText(ServiceTrust.ProfilePath), Environment.MachineName);
            InputSettings = profile.CreateInputOnlySettings();
            validatedSessionId = sessionId;
            IsActive = true;
            Common.RunOnLogonDesktop = true;
            Common.RunWithNoAdminRight = false;
            return true;
        }
        catch (Exception error)
        {
            // No disk logging, dialogs, fallback UI, settings or key material on failure.
            Debug.WriteLine("Secure desktop agent startup rejected: " + error.GetType().Name);
            Environment.ExitCode = 1;
            return false;
        }
    }

    internal static bool IsValidContext(bool isSystem, uint sessionId, uint activeSessionId,
        string station, string desktop, string executable, string fixedExecutable) => isSystem
        && ServiceTrust.IsInteractiveSession(sessionId) && sessionId == activeSessionId
        && string.Equals(station, "WinSta0", StringComparison.OrdinalIgnoreCase)
        && string.Equals(desktop, "Winlogon", StringComparison.OrdinalIgnoreCase)
        && string.Equals(executable, fixedExecutable, StringComparison.OrdinalIgnoreCase);

    internal static bool TryExport(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[1], ExportArgument, StringComparison.Ordinal)) return false;
        try
        {
            if (args.Length != 3 || IsSystem)
                throw new InvalidOperationException("Export a profile from the configured normal user copy, with one destination file.");
            var settings = PortableSettingsStore.Read(PortableApplication.CurrentSettingsPath);
            var profile = SecureDesktopProfile.FromPreferences(settings, Environment.MachineName);
            WriteExport(args[2], profile);
            Environment.ExitCode = 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Secure desktop profile export failed: " + error.Message);
            Environment.ExitCode = 1;
        }
        return true;
    }

    internal static void WriteExport(string destination, SecureDesktopProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate(profile.LocalMachineName);
        byte[] document = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        string finalPath = Path.GetFullPath(destination);
        string stagedPath = Path.Combine(Path.GetDirectoryName(finalPath)!, ".mwb-secure-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User);
        security.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        bool created = false;
        try
        {
            // Attach the private DACL atomically at creation. A write-only handle
            // cannot apply WRITE_DAC later, and plaintext must never be public.
            using (var output = FileSystemAclExtensions.Create(new FileInfo(stagedPath), FileMode.CreateNew,
                FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security))
            {
                created = true;
                output.Write(document);
                output.Flush(flushToDisk: true);
            }
            // A same-directory rename publishes only a complete private file and
            // refuses to replace an existing reviewed export or preferences file.
            File.Move(stagedPath, finalPath);
            created = false;
        }
        finally
        {
            if (created) File.Delete(stagedPath);
        }
    }

    internal static bool AllowsPacket(PackageType type) => type is PackageType.Hi or PackageType.Hello
        or PackageType.ByeBye or PackageType.Heartbeat or PackageType.Awake
        or PackageType.Heartbeat_ex or PackageType.Heartbeat_ex_l2 or PackageType.Heartbeat_ex_l3
        or PackageType.Keyboard or PackageType.Mouse
        or PackageType.Handshake or PackageType.HandshakeAck;

    internal static bool IsCurrentDesktopActive()
    {
        if (!IsActive) return true;
        // GetCursorPos fails unless this thread's desktop is the input desktop.
        // Check immediately before injection, including work queued before a switch.
        // The process session is immutable for this worker. Recreating Process
        // here performs an OS process-info query for every high-rate mouse packet.
        return validatedSessionId == WTSGetActiveConsoleSessionId()
            && GetCursorPos(out _);
    }

    internal static Mutex CreateSocketMutex(string name)
    {
        // The first process can be SYSTEM before sign-in. Both the normal user and
        // the protected worker need wait/release rights, never configuration IPC.
        const MutexRights useRights = MutexRights.Synchronize | MutexRights.Modify;
        try { return MutexAcl.OpenExisting(name, useRights); }
        catch (WaitHandleCannotBeOpenedException) { }
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), useRights, AccessControlType.Allow));
        try { return MutexAcl.Create(initiallyOwned: false, name, out _, security); }
        catch (UnauthorizedAccessException) { return MutexAcl.OpenExisting(name, useRights); }
    }

    private static string ObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return string.Empty;
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, name, name.Capacity * sizeof(char), out _) ? name.ToString() : string.Empty;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
}
