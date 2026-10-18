// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.SecureDesktop;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class SecureDesktopServiceTrustTests
{
    private const string Users = "S-1-5-32-545";
    private const string Administrators = "S-1-5-32-544";
    private const string System = "S-1-5-18";

    private static FileSystemAccessRule Allow(string sid, FileSystemRights rights) =>
        new(new SecurityIdentifier(sid), rights, AccessControlType.Allow);

    private static FileSystemAccessRule RawRule(string sddl)
    {
        // The public rule constructor rejects unmapped GENERIC_* masks, but a
        // real NTFS DACL may still contain them. Preserve their raw representation.
        // A directory descriptor also preserves inherit-only ACEs for testing.
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier))[0] as FileSystemAccessRule
            ?? throw new InvalidOperationException("The test security descriptor has no file-system rule.");
    }

    [TestMethod]
    public void OnlySystemAdministratorsAndTrustedInstallerAreTrusted()
    {
        foreach (string sid in new[] { System, Administrators, ServiceTrust.TrustedInstallerSid })
        {
            Assert.IsTrue(ServiceTrust.IsTrustedIdentity(sid), sid);
            Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(Allow(sid, FileSystemRights.FullControl), true, true), sid);
        }

        foreach (string sid in new[] { Users, "S-1-1-0", "S-1-5-11", "S-1-5-21-1-2-3-1001" })
            Assert.IsFalse(ServiceTrust.IsTrustedIdentity(sid), sid);

        Assert.IsFalse(ServiceTrust.IsTrustedIdentity(ServiceTrust.TrustedInstallerSid, allowTrustedInstaller: false));
        Assert.IsTrue(ServiceTrust.IsTrustedIdentity(System, allowTrustedInstaller: false));
        Assert.IsTrue(ServiceTrust.IsTrustedIdentity(Administrators, allowTrustedInstaller: false));
    }

    [TestMethod]
    public void UnprivilegedModificationOfProtectedProductObjectsIsRejected()
    {
        foreach (FileSystemRights rights in new[]
        {
            FileSystemRights.WriteData, FileSystemRights.AppendData,
            FileSystemRights.WriteAttributes, FileSystemRights.WriteExtendedAttributes,
            FileSystemRights.Delete, FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership,
            FileSystemRights.Modify, FileSystemRights.FullControl,
        })
        {
            Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(Allow(Users, rights), true, false), rights.ToString());
        }

        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(Allow(Users, FileSystemRights.ReadAndExecute), true, false));
    }

    [TestMethod]
    public void SharedAncestorsMayCreateUnrelatedChildrenButCannotReplaceProtectedDirectories()
    {
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(Allow(Users, FileSystemRights.CreateFiles), false, false));
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(Allow(Users, FileSystemRights.CreateDirectories), false, false));

        foreach (FileSystemRights rights in new[]
        {
            FileSystemRights.Delete, FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership,
        })
            Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(Allow(Users, rights), false, false), rights.ToString());
    }

    [TestMethod]
    public void PrivateProfileCannotExposeSharedKeyToUnprivilegedReaders()
    {
        var readable = Allow(Users, FileSystemRights.ReadAndExecute);
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(readable, true, false));
        Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(readable, true, true));
        Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(Allow("S-1-1-0", FileSystemRights.ReadData), true, true));
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(Allow(System, FileSystemRights.ReadData), true, true));
    }

    [TestMethod]
    public void InheritOnlyAndDenyEntriesDoNotGrantAccessToCurrentObject()
    {
        var inheritedOnly = new FileSystemAccessRule(new SecurityIdentifier(Users), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow);
        var denied = new FileSystemAccessRule(new SecurityIdentifier(Users), FileSystemRights.FullControl, AccessControlType.Deny);

        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(inheritedOnly, true, true));
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(denied, true, true));
        Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(Allow(Users, FileSystemRights.FullControl), true, true));
    }

    [TestMethod]
    public void RawGenericAccessMasksCannotBypassProtectedObjectChecks()
    {
        foreach (string mask in new[] { "GW", "GA" })
        {
            var grant = RawRule($"D:(A;;{mask};;;BU)");
            Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(grant, true, false), mask);
            Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(grant, false, false), mask + " ancestor");
            Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(grant, true, true), mask + " profile");
            Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(RawRule($"D:(D;;{mask};;;BU)"), true, true), mask + " deny");
            Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(RawRule($"D:(A;CIIO;{mask};;;BU)"), true, true), mask + " inherit-only");
            Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(RawRule($"D:(A;;{mask};;;BA)"), true, true), mask + " administrator");
        }

        var genericRead = RawRule("D:(A;;GR;;;BU)");
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(genericRead, true, false));
        Assert.IsTrue(ServiceTrust.IsUnsafeAccessRule(genericRead, true, true));
        Assert.IsFalse(ServiceTrust.IsUnsafeAccessRule(RawRule("D:(A;;GX;;;BU)"), true, true));
    }

    [TestMethod]
    public void SessionZeroAndDetachedConsoleCannotReceiveAgent()
    {
        Assert.IsFalse(ServiceTrust.IsInteractiveSession(0));
        Assert.IsFalse(ServiceTrust.IsInteractiveSession(uint.MaxValue));
        Assert.IsTrue(ServiceTrust.IsInteractiveSession(1));
        Assert.IsTrue(ServiceTrust.IsInteractiveSession(100));
    }

    [TestMethod]
    public void AgentLaunchCommandUsesOnlyQuotedFixedProgramFilesPathAndKnownArgument()
    {
        Assert.AreEqual(Path.Combine(ServiceTrust.InstallDirectory, "MouseWithoutBorders.exe"), ServiceTrust.AgentPath);
        Assert.AreEqual("\"" + ServiceTrust.AgentPath + "\" --secure-desktop-agent", ServiceTrust.AgentCommandLine);
        Assert.IsTrue(Path.IsPathFullyQualified(ServiceTrust.AgentPath));
        Assert.IsTrue(ServiceTrust.InstallDirectory.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }
}
