// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class SecureDesktopProfileTests
{
    private static SecureDesktopProfile ValidProfile() => new()
    {
        SecurityKey = "test-strong-key-1234",
        LocalMachineName = "TEST-PC",
        MachineId = 12345,
        MachineMatrix = new[] { "TEST-PC", "OTHER-PC", "", "" },
        MatrixOneRow = true,
        TcpPort = 15100,
        IpMappings = "OTHER-PC 192.168.2.2",
        EasyMouse = 1,
    };

    private static string Json(SecureDesktopProfile profile) => JsonSerializer.Serialize(profile);

    [TestMethod]
    public void InputOnlyProfilePreservesIdentityAndLayoutButNeverTransfersOrUi()
    {
        var profile = SecureDesktopProfile.Parse(Json(ValidProfile()), "test-pc");
        var settings = profile.CreateInputOnlySettings();
        Assert.AreEqual(12345, settings.Properties.MachineID.Value);
        Assert.AreEqual("test-strong-key-1234", settings.Properties.SecurityKey.Value);
        CollectionAssert.AreEqual(new[] { "TEST-PC", "OTHER-PC", "", "" }, settings.Properties.MachineMatrixString);
        Assert.IsFalse(settings.Properties.ShareClipboard);
        Assert.IsFalse(settings.Properties.TransferFile);
        Assert.IsFalse(settings.Properties.ShowOriginalUI);
        Assert.IsFalse(settings.Properties.FirstRun);
        Assert.IsFalse(settings.Properties.KeyboardShortcutsEnabled);
        Assert.IsFalse(settings.Properties.DrawMouseCursor);
        Assert.IsFalse(settings.Properties.BlockScreenSaverOnOtherMachines);
        Assert.AreEqual(string.Empty, settings.Properties.DefaultReceivingFolder);
    }

    [TestMethod]
    public void ExportAllowlistExcludesUntrustedPathsAndPortableApplicationSettings()
    {
        var prefs = new MouseWithoutBordersSettings();
        prefs.Properties.SecurityKey.Value = "four";
        prefs.Properties.MachineID.Value = 12345;
        prefs.Properties.MachineMatrixString = new() { "TEST-PC", "OTHER-PC", "", "" };
        prefs.Properties.DefaultReceivingFolder = @"C:\private\downloads";
        prefs.Properties.ShareClipboard = true;
        prefs.Properties.TransferFile = true;
        prefs.Properties.ShowOriginalUI = true;
        var exported = SecureDesktopProfile.FromPreferences(prefs, "TEST-PC");
        string json = Json(exported);
        Assert.IsFalse(json.Contains("private", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("DefaultReceivingFolder", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("UseService", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("ShareClipboard", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("TransferFile", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("ShowOriginalUI", StringComparison.Ordinal));
        Assert.AreEqual("four", SecureDesktopProfile.Parse(json, "TEST-PC").SecurityKey);
        Assert.IsTrue(prefs.Properties.ShareClipboard, "Export must not change the normal user's preferences.");
    }

    [TestMethod]
    public void ExtraMissingDuplicateOrCaseChangedFieldsFailClosed()
    {
        string json = Json(ValidProfile());
        foreach (string invalid in new[]
        {
            json[..^1] + ",\"executable\":\"C:\\\\user\\\\bad.exe\"}",
            json[..^1] + ",\"schemaVersion\":1}",
            json.Replace("\"schemaVersion\":1,", "", StringComparison.Ordinal),
            json.Replace("schemaVersion", "SchemaVersion", StringComparison.Ordinal),
            "null", "[]", "{}", "{invalid}",
        })
            Assert.ThrowsException<InvalidDataException>(() => SecureDesktopProfile.Parse(invalid, "TEST-PC"));
    }

    [TestMethod]
    public void ProfilesAreBoundedAndNeverAcceptAnotherComputerIdentity()
    {
        Assert.ThrowsException<InvalidDataException>(() => SecureDesktopProfile.Parse(new string(' ', SecureDesktopProfile.MaximumBytes + 1), "TEST-PC"));
        Assert.ThrowsException<InvalidDataException>(() => SecureDesktopProfile.Parse(Json(ValidProfile()), "ANOTHER-PC"));
        var p = ValidProfile();
        p.SchemaVersion = 2;
        Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
    }

    [TestMethod]
    public void InvalidKeysIdentityPortsAndModesAreRejected()
    {
        foreach (string key in new[] { "", "abc", "abcd\n", new string('a', 1025), null! })
        {
            var p = ValidProfile(); p.SecurityKey = key;
            Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
        }
        foreach (int id in new[] { 0, -1 })
        {
            var p = ValidProfile(); p.MachineId = id;
            Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
        }
        foreach (int port in new[] { -1, 0, 65535, 65536 })
        {
            var p = ValidProfile(); p.TcpPort = port;
            Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
        }
        foreach (int mode in new[] { -1, 4 })
        {
            var p = ValidProfile(); p.EasyMouse = mode;
            Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
        }
    }

    [TestMethod]
    public void InvalidOrAmbiguousMachineLayoutsAreRejected()
    {
        foreach (string[] matrix in new[]
        {
            new[] { "TEST-PC" }, new[] { "TEST-PC", "OTHER-PC", "other-pc", "" },
            new[] { "OTHER-PC", "", "", "" }, new[] { "TEST-PC", "A/B", "", "" },
            new[] { "TEST-PC", " OTHER-PC ", "", "" }, new[] { "TEST-PC", null!, "", "" },
        })
        {
            var p = ValidProfile(); p.MachineMatrix = matrix;
            Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
        }
    }

    [TestMethod]
    public void MappingStringsAreBoundedAndRejectEmbeddedControlCharacters()
    {
        foreach (string mapping in new[] { new string('a', 8193), "TEST-PC\0anything", null! })
        {
            var p = ValidProfile(); p.IpMappings = mapping;
            Assert.ThrowsException<InvalidDataException>(() => p.Validate("TEST-PC"));
        }
    }

    [TestMethod]
    public void AgentContextRequiresSystemFixedBinaryActiveSessionAndWinlogonStation()
    {
        const string binary = @"C:\Program Files\Mouse Without Borders Secure Desktop\MouseWithoutBorders.exe";
        Assert.IsTrue(SecureDesktopAgent.IsValidContext(true, 1, 1, "WinSta0", "Winlogon", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(false, 1, 1, "WinSta0", "Winlogon", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(true, 0, 0, "WinSta0", "Winlogon", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(true, uint.MaxValue, uint.MaxValue, "WinSta0", "Winlogon", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(true, 1, 2, "WinSta0", "Winlogon", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(true, 1, 1, "Service-0x0-3e7$", "Winlogon", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(true, 1, 1, "WinSta0", "Default", binary, binary));
        Assert.IsFalse(SecureDesktopAgent.IsValidContext(true, 1, 1, "WinSta0", "Winlogon", @"C:\Users\Alice\MouseWithoutBorders.exe", binary));
    }

    [TestMethod]
    public void LegacyDesktopArgumentsCannotBecomeASecondPrivilegedEntryPoint()
    {
        foreach (string arg in new[] { "winlogon", "Winlogon", "default", "UseService", "Screen-saver" })
            Assert.IsTrue(SecureDesktopAgent.IsLegacyServiceInvocation(new[] { "app.exe", arg }));
        Assert.IsFalse(SecureDesktopAgent.IsLegacyServiceInvocation(new[] { "app.exe" }));
        Assert.IsFalse(SecureDesktopAgent.IsLegacyServiceInvocation(new[] { "app.exe", PortableApplication.ClipboardHelperArgument }));
    }

    [TestMethod]
    public void SharedSocketMutexGrantsUsersOnlyWaitAndReleaseRights()
    {
        string name = "Local\\MouseWithoutBorders.SecureDesktop.Tests." + Guid.NewGuid().ToString("N");
        using var mutex = SecureDesktopAgent.CreateSocketMutex(name);
        var descriptor = System.Threading.MutexAcl.OpenExisting(name,
            System.Security.AccessControl.MutexRights.ReadPermissions);
        using (descriptor)
        {
            var security = descriptor.GetAccessControl();
            var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null);
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .OfType<System.Security.AccessControl.MutexAccessRule>()
                .Where(rule => rule.IdentityReference.Equals(users) && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow)
                .ToArray();
            Assert.AreEqual(1, rules.Length);
            Assert.AreEqual(System.Security.AccessControl.MutexRights.Synchronize | System.Security.AccessControl.MutexRights.Modify,
                rules[0].MutexRights);
        }
        using var second = SecureDesktopAgent.CreateSocketMutex(name);
        Assert.IsTrue(second.WaitOne(1000));
        second.ReleaseMutex();
    }

    [TestMethod]
    public void ExportWritesCompleteProfileWithPrivateAclFromCreation()
    {
        WithExportDirectory(directory =>
        {
            string destination = Path.Combine(directory, "profile.json");
            SecureDesktopAgent.WriteExport(destination, ValidProfile());
            var parsed = SecureDesktopProfile.Parse(File.ReadAllText(destination), "TEST-PC");
            Assert.AreEqual("test-strong-key-1234", parsed.SecurityKey);
            var security = new FileInfo(destination).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.IsTrue(security.AreAccessRulesProtected, "An export must not inherit public read access from its folder.");
            using var identity = WindowsIdentity.GetCurrent();
            Assert.AreEqual(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            var allowed = new HashSet<string> { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" };
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow)
                    Assert.IsTrue(allowed.Contains(((SecurityIdentifier)rule.IdentityReference).Value), "Unexpected access to the shared key.");
            CollectionAssert.AreEqual(new[] { "profile.json" }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
        });
    }

    [TestMethod]
    public void ExportNeverOverwritesAnExistingFileOrItsAcl()
    {
        WithExportDirectory(directory =>
        {
            string destination = Path.Combine(directory, "existing.json");
            File.WriteAllText(destination, "keep the reviewed export unchanged");
            byte[] originalAcl = new FileInfo(destination).GetAccessControl().GetSecurityDescriptorBinaryForm();
            Assert.ThrowsException<IOException>(() => SecureDesktopAgent.WriteExport(destination, ValidProfile()));
            Assert.AreEqual("keep the reviewed export unchanged", File.ReadAllText(destination));
            CollectionAssert.AreEqual(originalAcl, new FileInfo(destination).GetAccessControl().GetSecurityDescriptorBinaryForm());
            CollectionAssert.AreEqual(new[] { "existing.json" }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
        });
    }

    [TestMethod]
    public void FailedExportCleansPrivateStagingAndPreservesExistingDirectory()
    {
        WithExportDirectory(directory =>
        {
            string destination = Path.Combine(directory, "existing-directory");
            Directory.CreateDirectory(destination);
            Assert.ThrowsException<IOException>(() => SecureDesktopAgent.WriteExport(destination, ValidProfile()));
            Assert.IsTrue(Directory.Exists(destination));
            Assert.AreEqual(0, Directory.GetFiles(directory).Length, "A failed export must leave no partial or staging file.");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(destination).Length);
        });
    }

    [TestMethod]
    public void InvalidExportProfileNeverCreatesTheDestination()
    {
        WithExportDirectory(directory =>
        {
            var profile = ValidProfile();
            profile.SecurityKey = "bad";
            Assert.ThrowsException<InvalidDataException>(() => SecureDesktopAgent.WriteExport(Path.Combine(directory, "profile.json"), profile));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(directory).Length);
        });
    }

    private static void WithExportDirectory(Action<string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MwbSecureExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
