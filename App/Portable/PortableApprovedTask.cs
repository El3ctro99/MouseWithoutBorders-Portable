// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MouseWithoutBorders.Core;

/// <summary>
/// A Windows-approved, same-user on-demand task. Its only action is the protected
/// native launcher; portable files and caller arguments never become task actions.
/// </summary>
internal static class PortableApprovedTask
{
    internal const string TaskFolder = @"\MouseWithoutBorders Portable\";
    internal const string MainFileName = "MouseWithoutBorders.exe";
    internal const string LauncherFileName = "MwbApprovedLauncher.exe";
    private const string SourceFileName = "source.txt";
    private const string ManifestFileName = "payload.sha256";
    private const string LauncherResource = "MouseWithoutBorders.ApprovedLauncher.exe";
    private const string Administrators = "S-1-5-32-544";
    private const string SystemSid = "S-1-5-18";
    private const string OwnerRights = "S-1-3-4";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const uint ReadOnlyMask = 0xA01200A9; // GR, GX and mapped file RX.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string RootDirectory => Path.Combine(ProgramFilesDirectory,
        "Mouse Without Borders Approved Launch", CurrentUserSid);
    internal static string TaskName => "Approved-" + CurrentUserSid;

    internal static string ReadSourceExecutable()
    {
        RequireNormalUser();
        return ReadValidatedAssociation();
    }

    internal static bool HasApproval(string sourceExecutable)
    {
        try
        {
            RequireNormalUser();
            string source = CanonicalSource(sourceExecutable);
            ValidateAssociation(source);
            using var connection = TaskConnection.Open(createFolder: false);
            return connection.Task != null && ValidTask(connection.Task, source, requireEnabled: false);
        }
        catch (Exception) { return false; }
    }

    internal static bool IsConfigured(string sourceExecutable)
    {
        try
        {
            RequireNormalUser();
            string source = CanonicalSource(sourceExecutable);
            ValidateInstallation(source);
            using var connection = TaskConnection.Open(createFolder: false);
            return connection.Task != null && ValidTask(connection.Task, source);
        }
        catch (Exception) { return false; }
    }

    internal static bool TryRun(string sourceExecutable)
    {
        try
        {
            RequireNormalUser();
            string source = CanonicalSource(sourceExecutable);
            ValidateInstallation(source);
            // A changed ordinary EXE must be approved again. Never silently run
            // the old protected version after a normal application update.
            if (!HashFile(source).Equals(HashFile(Path.Combine(RootDirectory, MainFileName)), StringComparison.Ordinal))
                return false;
            using var connection = TaskConnection.Open(createFolder: false);
            if (connection.Task == null || !ValidTask(connection.Task, source)) return false;
            object? running = null;
            try
            {
                running = connection.Task.Run(null);
                return running != null; // The caller independently verifies the High app.
            }
            finally { Release(running); }
        }
        catch (Exception) { return false; }
    }

    internal static void Enable(string sourceExecutable)
    {
        RequireAdministrator();
        string source = CanonicalSource(sourceExecutable);
        RequireSetupSource(source);
        RequireNoReparseAncestors(source);
        byte[] launcher = ReadLauncherResource();
        string sourceHash;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            sourceHash = Convert.ToHexString(SHA256.HashData(input));
        string launcherHash = Convert.ToHexString(SHA256.HashData(launcher));
        ValidateProtectedAncestors();
        EnsureProtectedDirectory(Path.GetDirectoryName(RootDirectory)!, baseDirectory: true);
        EnsureProtectedDirectory(RootDirectory);

        string sourceRecord = Path.Combine(RootDirectory, SourceFileName);
        if (File.Exists(sourceRecord))
        {
            ValidateEntry(new FileInfo(sourceRecord));
            if (!ReadSourceRecord().Equals(source, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Approved launch is already associated with a different application location.");
        }

        using var connection = TaskConnection.Open(createFolder: true);
        bool existed = connection.Task != null;
        if (existed)
        {
            ValidateAssociation(source);
            if (!ValidTask(connection.Task, source, requireEnabled: false))
                throw new SecurityException("The existing approved launch task does not match this application.");
        }
        string mainPath = Path.Combine(RootDirectory, MainFileName);
        string launcherPath = Path.Combine(RootDirectory, LauncherFileName);
        foreach (string path in new[] { mainPath, launcherPath })
            if (File.Exists(path)) ValidateEntry(new FileInfo(path));
        bool changingPayload = !File.Exists(mainPath) || !File.Exists(launcherPath)
            || !HashFile(mainPath).Equals(sourceHash, StringComparison.Ordinal)
            || !HashFile(launcherPath).Equals(launcherHash, StringComparison.Ordinal);
        if (changingPayload && ProtectedCopyIsRunning())
            throw new InvalidOperationException("Close the approved copy and reopen the ordinary installed copy before updating approved launch.");
        bool createdTask = false;
        bool previousEnabled = existed && connection.Task.Enabled;
        try
        {
            if (existed && changingPayload) connection.Task.Enabled = false;
            EnsureProtectedDirectory(Path.Combine(RootDirectory, "runtime"));
            EnsureProtectedDirectory(Path.Combine(RootDirectory, "runtime", "temp"));
            EnsureProtectedDirectory(Path.Combine(RootDirectory, "runtime", "bundle"));
            if (changingPayload)
            {
                WriteProtectedFile(mainPath, stream =>
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    input.CopyTo(stream);
                });
                if (!HashFile(mainPath).Equals(sourceHash, StringComparison.Ordinal))
                    throw new SecurityException("The approved application copy changed while it was being prepared.");
                WriteProtectedFile(launcherPath, stream => stream.Write(launcher));
            }
            WriteProtectedFile(sourceRecord, stream => stream.Write(StrictUtf8.GetBytes(source)));
            string manifest = sourceHash + "  " + MainFileName + "\n" + launcherHash + "  " + LauncherFileName + "\n";
            WriteProtectedFile(Path.Combine(RootDirectory, ManifestFileName), stream => stream.Write(StrictUtf8.GetBytes(manifest)));
            ValidateInstallation(source);

            object? definition = null;
            object? registered = null;
            try
            {
                definition = connection.Service.NewTask(0);
                dynamic task = definition;
                task.RegistrationInfo.Description = "Runs this user's protected Mouse Without Borders copy after Windows approval.";
                task.Principal.UserId = CurrentUserSid;
                task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN. No password is stored.
                task.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST.
                task.Settings.Enabled = true;
                task.Settings.AllowDemandStart = true;
                task.Settings.DisallowStartIfOnBatteries = false;
                task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.ExecutionTimeLimit = "PT0S";
                task.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW.
                task.Settings.RunOnlyIfIdle = false;
                task.Settings.RunOnlyIfNetworkAvailable = false;
                task.Settings.WakeToRun = false;
                dynamic action = task.Actions.Create(0); // TASK_ACTION_EXEC.
                try
                {
                    action.Path = launcherPath;
                    action.Arguments = string.Empty;
                    action.WorkingDirectory = RootDirectory;
                }
                finally { Release(action); }
                registered = connection.Folder.RegisterTaskDefinition(TaskName, definition, RegistrationFlags(existed),
                    CurrentUserSid, null, 3, TaskSecurityDescriptor);
                createdTask = !existed;
                if (!ValidTask(registered, source))
                    throw new SecurityException("Windows did not retain the restricted approved launch task.");
            }
            finally { Release(registered); Release(definition); }
        }
        catch
        {
            // Keep protected files for inspection/retry. Never recursively delete
            // directories or remove an existing task as setup-failure cleanup.
            if (createdTask)
            {
                try { connection.Folder.DeleteTask(TaskName, 0); }
                catch (Exception) { }
            }
            else if (existed && previousEnabled)
            {
                try
                {
                    ValidateInstallation(source);
                    if (ValidTask(connection.Task, source, requireEnabled: false)) connection.Task.Enabled = true;
                }
                catch (Exception) { }
            }
            throw;
        }
    }

    internal static void Disable(string sourceExecutable)
    {
        RequireAdministrator();
        string source = CanonicalSource(sourceExecutable);
        RequireSetupSource(source);
        ValidateAssociation(source);
        using var connection = TaskConnection.Open(createFolder: false);
        if (connection.Task == null) return;
        if (!ValidTask(connection.Task, source, requireEnabled: false))
            throw new SecurityException("The approved launch task does not match this application.");
        connection.Folder.DeleteTask(TaskName, 0);
        // The protected payload may still be running. Leave it and its cache in
        // place; disabling removes only this user's fixed on-demand task.
    }

    private static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new SecurityException("The Windows user could not be identified.");
        }
    }

    private static string ProgramFilesDirectory
    {
        get
        {
            Guid folder = new("905e63b6-c1bf-494e-b29c-65b732d3d21a");
            int error = SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out IntPtr pointer);
            if (error != 0) Marshal.ThrowExceptionForHR(error);
            try { return Path.GetFullPath(Marshal.PtrToStringUni(pointer) ?? throw new SecurityException("Program Files is unavailable.")); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
    }

    internal static string CanonicalSource(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)
            || executable.StartsWith(@"\\", StringComparison.Ordinal) || executable.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new SecurityException("Approved launch requires a local absolute application path.");
        string full = Path.GetFullPath(executable);
        if (!Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || full.AsSpan(2).Contains(':') || new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new SecurityException("Approved launch requires the local Mouse Without Borders executable.");
        return full;
    }

    private static void RequireNormalUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (SecureDesktopAgent.IsActive || identity.User == null || identity.IsSystem
            || identity.User.IsWellKnown(WellKnownSidType.LocalServiceSid)
            || identity.User.IsWellKnown(WellKnownSidType.NetworkServiceSid))
            throw new SecurityException("Approved launch is available only to an interactive Windows user.");
    }

    private static void RequireAdministrator()
    {
        RequireNormalUser();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new SecurityException("Windows administrator approval is required to change approved launch.");
    }

    private static void RequireSetupSource(string source)
    {
        string current = Path.GetFullPath(PortableApplication.CurrentExecutablePath);
        if (current.Equals(source, StringComparison.OrdinalIgnoreCase)) return;
        if (current.Equals(Path.Combine(RootDirectory, MainFileName), StringComparison.OrdinalIgnoreCase)
            && ReadSourceExecutable().Equals(source, StringComparison.OrdinalIgnoreCase)) return;
        throw new SecurityException("Approved launch setup must run from this application copy.");
    }

    private static string ReadSourceRecord()
    {
        string text = StrictUtf8.GetString(ReadBounded(Path.Combine(RootDirectory, SourceFileName), 32768));
        string canonical = CanonicalSource(text);
        if (!canonical.Equals(text, StringComparison.Ordinal)) throw new SecurityException("The approved source record is not canonical.");
        return canonical;
    }

    private static string ReadValidatedAssociation()
    {
        // Repair/removal needs proof of the original source association, even
        // when a previous payload replacement stopped before its manifest write.
        ValidateProtectedAncestors();
        RequireNoReparseAncestors(RootDirectory);
        ValidateEntry(new DirectoryInfo(Path.GetDirectoryName(RootDirectory)!), requireUserRead: false);
        ValidateEntry(new DirectoryInfo(RootDirectory));
        ValidateEntry(new FileInfo(Path.Combine(RootDirectory, SourceFileName)));
        return ReadSourceRecord();
    }

    private static void ValidateAssociation(string source)
    {
        if (!ReadValidatedAssociation().Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Approved launch belongs to another application location.");
    }

    private static void ValidateInstallation(string source)
    {
        ValidatePayloadEntries(requireManifest: true);
        if (!ReadSourceRecord().Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Approved launch belongs to another application location.");
        string[] lines = StrictUtf8.GetString(ReadBounded(Path.Combine(RootDirectory, ManifestFileName), 512)).Split('\n');
        if (lines.Length != 3 || lines[2].Length != 0)
            throw new SecurityException("The approved payload manifest is invalid.");
        ValidateManifestLine(lines[0], MainFileName);
        ValidateManifestLine(lines[1], LauncherFileName);
    }

    private static void ValidateManifestLine(string line, string name)
    {
        if (line.Length != 66 + name.Length || !line.AsSpan(64).SequenceEqual(("  " + name).AsSpan()))
            throw new SecurityException("The approved payload manifest is invalid.");
        string hash = line[..64];
        foreach (char value in hash)
            if (value is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
                throw new SecurityException("The approved payload manifest is invalid.");
        if (!HashFile(Path.Combine(RootDirectory, name)).Equals(hash, StringComparison.Ordinal))
            throw new SecurityException("An approved payload does not match its protected manifest.");
    }

    private static void ValidatePayloadEntries(bool requireManifest)
    {
        ValidateProtectedAncestors();
        RequireNoReparseAncestors(RootDirectory);
        ValidateEntry(new DirectoryInfo(Path.GetDirectoryName(RootDirectory)!), requireUserRead: false);
        foreach (string directory in new[] { RootDirectory, Path.Combine(RootDirectory, "runtime"),
            Path.Combine(RootDirectory, "runtime", "temp"), Path.Combine(RootDirectory, "runtime", "bundle") })
            ValidateEntry(new DirectoryInfo(directory));
        foreach (string name in new[] { MainFileName, LauncherFileName, SourceFileName })
            ValidateEntry(new FileInfo(Path.Combine(RootDirectory, name)));
        if (requireManifest) ValidateEntry(new FileInfo(Path.Combine(RootDirectory, ManifestFileName)));
    }

    private static void RequireNoReparseAncestors(string path)
    {
        string? cursor = Path.GetFullPath(path);
        while (cursor != null)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("Approved launch does not support symbolic links or junctions.");
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    private static void ValidateProtectedAncestors()
    {
        // A child's restrictive DACL does not prevent its replacement when an
        // ancestor grants DELETE_CHILD. Validate the existing Program Files path
        // through the drive root before installing or trusting the task action.
        for (var directory = new DirectoryInfo(ProgramFilesDirectory); directory != null; directory = directory.Parent)
        {
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("An approved launch ancestor is absent or is a reparse point.");
            var security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.Owner?.Value is not (Administrators or SystemSid or TrustedInstaller) || descriptor.DiscretionaryAcl == null)
                throw new SecurityException("An approved launch ancestor has an untrusted owner or access list.");
            foreach (GenericAce entry in descriptor.DiscretionaryAcl)
            {
                if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
                if (entry is QualifiedAce denied && denied.AceQualifier == AceQualifier.AccessDenied) continue;
                if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.IsCallback)
                    throw new SecurityException("An approved launch ancestor has an unsupported access rule.");
                string sid = ace.SecurityIdentifier.Value;
                if (sid is Administrators or SystemSid or TrustedInstaller) continue;
                uint safe = ReadOnlyMask | 0x116; // Create children/EA/attributes cannot replace this protected child.
                if ((unchecked((uint)ace.AccessMask) & ~safe) != 0)
                    throw new SecurityException("A non-administrator can replace or change an approved launch ancestor.");
            }
        }
    }

    private static void ValidateEntry(FileSystemInfo entry, bool requireUserRead = true)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("An approved launch path is absent or is a reparse point.");
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateDescriptor(new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0), requireUserRead, requireOwnerRights: true);
    }

    // Storage-only policy seam: no filesystem, task registration or elevation.
    internal static void ValidateDescriptor(RawSecurityDescriptor descriptor, bool requireUserRead, bool requireOwnerRights)
    {
        if (descriptor.Owner?.Value is not (Administrators or SystemSid)
            || descriptor.DiscretionaryAcl == null
            || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new SecurityException("Approved launch requires an administrator-owned protected access list.");
        bool administratorFull = false, systemFull = false, userRead = false, ownerReadOnly = false;
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.IsCallback)
                throw new SecurityException("The approved launch access list contains an unsupported rule.");
            string sid = ace.SecurityIdentifier.Value;
            uint mask = unchecked((uint)ace.AccessMask);
            bool effective = (ace.AceFlags & AceFlags.InheritOnly) == 0;
            bool full = (mask & 0x10000000) != 0 || (mask & 0x001F01FF) == 0x001F01FF;
            if (sid == Administrators) { administratorFull |= effective && full; continue; }
            if (sid == SystemSid) { systemFull |= effective && full; continue; }
            if (sid == OwnerRights)
            {
                if (mask != 0x00020000) throw new SecurityException("The owner-rights rule grants unsupported permissions.");
                ownerReadOnly |= effective;
                continue;
            }
            if ((mask & ~ReadOnlyMask) != 0)
                throw new SecurityException("A non-administrator can modify an approved launch entry.");
            if (sid == CurrentUserSid && effective && (mask & (0x80000000 | 1)) != 0 && (mask & (0x20000000 | 0x20)) != 0)
                userRead = true;
        }
        if (!administratorFull || !systemFull || (requireUserRead && !userRead) || (requireOwnerRights && !ownerReadOnly))
            throw new SecurityException("The approved launch access list is incomplete.");
    }

    private static string FileSecurityDescriptor(bool directory, bool baseDirectory = false)
    {
        string inheritance = directory ? "OICI" : string.Empty;
        string reader = baseDirectory ? "BU" : CurrentUserSid;
        return "O:BAG:BAD:P(A;" + inheritance + ";FA;;;SY)(A;" + inheritance + ";FA;;;BA)(A;"
            + inheritance + ";FRFX;;;" + reader + ")(A;" + inheritance + ";RC;;;OW)";
    }

    private static string TaskSecurityDescriptor => "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;" + CurrentUserSid + ")";

    private static void EnsureProtectedDirectory(string path, bool baseDirectory = false)
    {
        RequireNoReparseAncestors(path);
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(FileSecurityDescriptor(directory: true, baseDirectory));
            new DirectoryInfo(path).Create(security);
        }
        ValidateEntry(new DirectoryInfo(path), requireUserRead: !baseDirectory);
    }

    private static void WriteProtectedFile(string target, Action<Stream> write)
    {
        RequireNoReparseAncestors(target);
        if (File.Exists(target)) ValidateEntry(new FileInfo(target));
        string staging = Path.Combine(RootDirectory, ".approved-" + Guid.NewGuid().ToString("N") + ".tmp");
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(FileSecurityDescriptor(directory: false));
        bool created = false;
        try
        {
            using (var output = FileSystemAclExtensions.Create(new FileInfo(staging), FileMode.CreateNew,
                FileSystemRights.Write, FileShare.None, 65536, FileOptions.None, security))
            {
                created = true;
                write(output);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(target)) File.Replace(staging, target, null);
            else File.Move(staging, target);
            created = false;
            ValidateEntry(new FileInfo(target));
        }
        finally { if (created) File.Delete(staging); }
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > limit) throw new SecurityException("An approved launch record has an invalid size.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadLauncherResource()
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(LauncherResource)
            ?? throw new InvalidOperationException("This build does not contain the approved native launcher.");
        if (input.Length is <= 0 or > 8388608) throw new SecurityException("The embedded native launcher has an invalid size.");
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static bool ProtectedCopyIsRunning()
    {
        string expected = Path.Combine(RootDirectory, MainFileName);
        foreach (Process process in Process.GetProcessesByName("MouseWithoutBorders"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    IntPtr handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == IntPtr.Zero)
                    {
                        if (process.HasExited) continue;
                        throw new SecurityException("A running application could not be checked before replacing the approved copy.");
                    }
                    try
                    {
                        var image = new StringBuilder(32768);
                        uint length = (uint)image.Capacity;
                        if (!QueryFullProcessImageName(handle, 0, image, ref length))
                        {
                            if (process.HasExited) continue;
                            throw new SecurityException("A running application could not be checked before replacing the approved copy.");
                        }
                        if (image.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    finally { _ = CloseHandle(handle); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { throw new SecurityException("A running application could not be checked before replacing the approved copy."); }
            }
        }
        return false;
    }

    private static bool ValidTask(dynamic task, string source, bool requireEnabled = true)
    {
        if (task == null) return false;
        ValidateDescriptor(new RawSecurityDescriptor((string)task.GetSecurityDescriptor(7)), requireUserRead: true, requireOwnerRights: false);
        object? definitionObject = null;
        object? actionObject = null;
        try
        {
            definitionObject = task.Definition;
            dynamic definition = definitionObject;
            if (!string.IsNullOrEmpty((string?)definition.Principal.GroupId)
                || (int)definition.Actions.Count != 1) return false;
            actionObject = definition.Actions.Item(1);
            dynamic action = actionObject;
            string principal = ResolveTaskUser((string)definition.Principal.UserId);
            var policy = new TaskPolicy(principal, (int)definition.Principal.LogonType, (int)definition.Principal.RunLevel,
                (int)definition.Triggers.Count, (int)definition.Actions.Count, (int)action.Type,
                (string)action.Path, (string?)action.Arguments, (string)action.WorkingDirectory,
                (bool)definition.Settings.AllowDemandStart, (string)definition.XmlText, (bool)task.Enabled);
            return MatchesTaskPolicy(policy, CurrentUserSid, RootDirectory, requireEnabled)
                && ReadSourceRecord().Equals(source, StringComparison.OrdinalIgnoreCase);
        }
        finally { Release(actionObject); Release(definitionObject); }
    }

    internal readonly record struct TaskPolicy(string UserSid, int LogonType, int RunLevel,
        int TriggerCount, int ActionCount, int ActionType, string ActionPath, string? Arguments,
        string WorkingDirectory, bool AllowDemandStart, string Xml, bool Enabled);

    internal static bool MatchesTaskPolicy(TaskPolicy policy, string expectedUserSid, string root, bool requireEnabled = true)
    {
        // The projection contains no COM objects and can be checked without
        // registering or starting a task. Expected SID/root come from Windows.
        return !string.IsNullOrEmpty(policy.Xml) && policy.UserSid == expectedUserSid && policy.UserSid is not (SystemSid or "S-1-5-19" or "S-1-5-20")
            && policy.LogonType == 3 && policy.RunLevel == 1 && policy.TriggerCount == 0
            && policy.ActionCount == 1 && policy.ActionType == 0 && policy.AllowDemandStart
            && (!requireEnabled || policy.Enabled) && !policy.Xml.Contains("$(", StringComparison.Ordinal)
            && string.Equals(policy.ActionPath, Path.Combine(root, LauncherFileName), StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(policy.Arguments)
            && string.Equals(policy.WorkingDirectory, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTaskUser(string user)
    {
        if (user.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) return new SecurityIdentifier(user).Value;
        return ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private sealed class TaskConnection : IDisposable
    {
        internal dynamic Service { get; private set; } = null!;
        internal dynamic Folder { get; private set; } = null!;
        internal dynamic? Task { get; private set; }

        internal static TaskConnection Open(bool createFolder)
        {
            var result = new TaskConnection();
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
                result.Service = Activator.CreateInstance(type)!;
                result.Service.Connect();
                try { result.Folder = result.Service.GetFolder(TaskFolder.TrimEnd('\\')); }
                catch (Exception error) when (Missing(error) && createFolder)
                {
                    object? root = null;
                    try
                    {
                        root = result.Service.GetFolder(@"\");
                        result.Folder = ((dynamic)root).CreateFolder(TaskFolder.Trim('\\'), TaskSecurityDescriptor);
                    }
                    finally { Release(root); }
                }
                var descriptor = new RawSecurityDescriptor((string)result.Folder.GetSecurityDescriptor(7));
                ValidateDescriptor(descriptor, requireUserRead: !createFolder, requireOwnerRights: false);
                if (createFolder)
                {
                    // A shared fixed folder may already contain another user's
                    // task. Preserve its read rules and add only this user's RX.
                    bool currentRead = false;
                    foreach (GenericAce entry in descriptor.DiscretionaryAcl!)
                        if (entry is CommonAce ace && ace.SecurityIdentifier.Value == CurrentUserSid
                            && (unchecked((uint)ace.AccessMask) & 0xA0000000) == 0xA0000000) currentRead = true;
                    if (!currentRead)
                    {
                        var ace = new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0xA0000000),
                            new SecurityIdentifier(CurrentUserSid), false, null);
                        descriptor.DiscretionaryAcl!.InsertAce(descriptor.DiscretionaryAcl.Count, ace);
                        result.Folder.SetSecurityDescriptor(descriptor.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access), 0);
                    }
                    ValidateDescriptor(new RawSecurityDescriptor((string)result.Folder.GetSecurityDescriptor(7)), requireUserRead: true, requireOwnerRights: false);
                }
                try { result.Task = result.Folder.GetTask(TaskName); }
                catch (Exception error) when (Missing(error)) { result.Task = null; }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static bool Missing(Exception error) => IsMissingTask(error);
        public void Dispose() { Release(Task); Release(Folder); Release(Service); Task = null; Folder = null!; Service = null!; }
    }

    // COM interop maps these HRESULTs to FileNotFoundException or
    // DirectoryNotFoundException, rather than necessarily COMException.
    internal static bool IsMissingTask(Exception error) => error.HResult == unchecked((int)0x80070002)
        || error.HResult == unchecked((int)0x80070003);

    // Preserve the explicit read/execute-only principal ACE; otherwise Windows
    // may add its own principal grant when registering or updating the task.
    internal static int RegistrationFlags(bool existed) => (existed ? 6 : 2) | 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
#endif
