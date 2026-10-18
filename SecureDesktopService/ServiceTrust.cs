// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MouseWithoutBorders.SecureDesktop;

/// <summary>
/// Fixed, administrator-owned trust boundary shared by the service and its agent.
/// No executable, command line, session or profile path is accepted from an IPC client.
/// </summary>
public static class ServiceTrust
{
    public const string ServiceName = "MouseWithoutBorders.SecureDesktop";
    public const string AgentArgument = "--secure-desktop-agent";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const int MaximumProfileBytes = 64 * 1024;

    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mouse Without Borders Secure Desktop");
    public static string AgentPath => Path.Combine(InstallDirectory, "MouseWithoutBorders.exe");
    public static string ServicePath => Path.Combine(InstallDirectory, "MouseWithoutBorders.SecureDesktop.Service.exe");
    public static string ProfileDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MouseWithoutBorders", "SecureDesktop");
    public static string ProfilePath => Path.Combine(ProfileDirectory, "MouseWithoutBorders.secure.json");

    public static bool IsTrustedIdentity(string sid, bool allowTrustedInstaller = true) =>
        string.Equals(sid, "S-1-5-18", StringComparison.Ordinal) ||
        string.Equals(sid, "S-1-5-32-544", StringComparison.Ordinal) ||
        (allowTrustedInstaller && string.Equals(sid, TrustedInstallerSid, StringComparison.Ordinal));

    /// <summary>Inherit-only entries do not grant access to the object being checked.</summary>
    public static bool IsUnsafeAccessRule(FileSystemAccessRule rule, bool includeCreateRights, bool requirePrivateRead)
    {
        if (rule.AccessControlType != AccessControlType.Allow ||
            (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
            IsTrustedIdentity(((SecurityIdentifier)rule.IdentityReference).Value))
        {
            return false;
        }

        FileSystemRights unsafeRights = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
        if (includeCreateRights)
        {
            unsafeRights |= FileSystemRights.WriteData | FileSystemRights.AppendData |
                FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        }

        if (requirePrivateRead)
        {
            unsafeRights |= FileSystemRights.ReadData;
        }

        // A DACL can preserve raw GENERIC_* ACE bits instead of the filesystem's
        // mapped rights. Those grants are dangerous even though they have no
        // named FileSystemRights enum value.
        uint genericRights = unchecked((uint)rule.FileSystemRights);
        bool unsafeGeneric = (genericRights & 0x50000000U) != 0 ||
            (requirePrivateRead && (genericRights & 0x80000000U) != 0);
        return unsafeGeneric || (rule.FileSystemRights & unsafeRights) != 0;
    }

    public static bool IsInteractiveSession(uint sessionId) => sessionId != 0 && sessionId != uint.MaxValue;

    public static string AgentCommandLine => "\"" + AgentPath + "\" " + AgentArgument;

    public static void RequireLocalSystem()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
        {
            throw new SecurityException("The secure desktop worker requires LocalSystem.");
        }
    }

    public static void ValidateInstallation(bool requireServiceExecutable = true)
    {
        ValidateAncestors(InstallDirectory);
        ValidateAncestors(ProfileDirectory);
        ValidateProtectedObject(new DirectoryInfo(InstallDirectory), includeCreateRights: true, requirePrivateRead: false, allowTrustedInstallerOwner: false);
        ValidateProtectedObject(new DirectoryInfo(ProfileDirectory), includeCreateRights: true, requirePrivateRead: true, allowTrustedInstallerOwner: false);
        ValidateProtectedObject(new FileInfo(AgentPath), includeCreateRights: true, requirePrivateRead: false, allowTrustedInstallerOwner: false);
        if (requireServiceExecutable)
        {
            ValidateProtectedObject(new FileInfo(ServicePath), includeCreateRights: true, requirePrivateRead: false, allowTrustedInstallerOwner: false);
            ValidateRuntimeTree();
        }

        var profile = new FileInfo(ProfilePath);
        ValidateProtectedObject(profile, includeCreateRights: true, requirePrivateRead: true, allowTrustedInstallerOwner: false);
        if (profile.Length <= 0 || profile.Length > MaximumProfileBytes)
        {
            throw new SecurityException("The protected input profile has an invalid size.");
        }
    }

    public static void ValidatePrivateRuntimeDirectory(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), Path.Combine(ProfileDirectory, "runtime"), StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("The extraction directory must be the fixed protected runtime directory.");
        }

        ValidateAncestors(path);
        ValidateProtectedObject(new DirectoryInfo(path), includeCreateRights: true, requirePrivateRead: true, allowTrustedInstallerOwner: false);
    }

    private static void ValidateRuntimeTree()
    {
        // The optional self-contained service ships runtime DLLs, not a bundled
        // service EXE that could extract before Main into a shared temp directory.
        // Existing DLLs need their own checks even when their parent is protected.
        var pending = new System.Collections.Generic.Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(InstallDirectory));
        int checkedEntries = 0;
        while (pending.Count != 0)
        {
            foreach (FileSystemInfo entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (++checkedEntries > 4096) throw new SecurityException("The protected service installation has too many entries.");
                ValidateProtectedObject(entry, includeCreateRights: true, requirePrivateRead: false, allowTrustedInstallerOwner: false);
                if (entry is DirectoryInfo directory) pending.Push(directory);
            }
        }
    }

    private static void ValidateAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
        {
            // Creation of unrelated children at a drive root or ProgramData does not
            // permit replacing an existing protected product directory. Delete-child,
            // ownership and ACL modification there would permit such replacement.
            ValidateProtectedObject(directory, includeCreateRights: false, requirePrivateRead: false, allowTrustedInstallerOwner: true);
        }
    }

    private static void ValidateProtectedObject(FileSystemInfo entry, bool includeCreateRights, bool requirePrivateRead, bool allowTrustedInstallerOwner)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException("A protected service object is missing or is a reparse point.");
        }

        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier? owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !IsTrustedIdentity(owner.Value, allowTrustedInstallerOwner))
        {
            throw new SecurityException("A protected service object has an untrusted owner.");
        }

        // Reject null DACLs (everyone full control), and require a real restrictive
        // DACL rather than assuming an empty managed rule list means no access.
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if ((raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || raw.DiscretionaryAcl == null)
        {
            throw new SecurityException("A protected service object has no restrictive DACL.");
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (IsUnsafeAccessRule(rule, includeCreateRights, requirePrivateRead))
            {
                throw new SecurityException("A protected service object grants untrusted write or profile-read access.");
            }
        }
    }
}
