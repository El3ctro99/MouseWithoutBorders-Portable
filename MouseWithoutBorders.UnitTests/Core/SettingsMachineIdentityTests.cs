// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using MwbSettings = MouseWithoutBorders.Class.Settings;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class SettingsMachineIdentityTests
{
    private const int LocalId = 12345;
    private const uint PeerId = 67890;
    private string folder = null!;
    private string path = null!;
    private bool previousLogonDesktop;

    [TestInitialize]
    public void Setup()
    {
        previousLogonDesktop = Common.RunOnLogonDesktop;
        Common.RunOnLogonDesktop = false;
        folder = Path.Combine(Path.GetTempPath(), "mwb-machine-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "prefs.json");
        var document = new MouseWithoutBordersSettings();
        document.Properties.SecurityKey.Value = "test-identity-key";
        document.Properties.MachineID.Value = LocalId;
        document.Properties.MachineMatrixString = new() { "LOCAL-PC", "PEER-PC", "", "" };
        document.Properties.MachinePool.Value = $"LOCAL-PC:{LocalId},PEER-PC:{PeerId},:,:";
        PortableSettingsStore.Write(path, JsonSerializer.Serialize(document, SettingsUtils.SerializerOptions));
    }

    [TestCleanup]
    public void Cleanup()
    {
        Common.RunOnLogonDesktop = previousLogonDesktop;
        Directory.Delete(folder, recursive: true);
    }

    private MwbSettings Load() => new(new SettingsUtils(path), watchChanges: false);

    [TestMethod]
    public void DestinationSetBeforeLocalIdentityReadCannotAdoptPeerIdentity()
    {
        var settings = Load();
        settings.DesMachineID = PeerId;
        Assert.AreEqual(PeerId, settings.DesMachineID);
        Assert.AreEqual(LocalId, settings.MachineId);
        settings.SaveSettingsSynchronously();
        Assert.AreEqual(LocalId, PortableSettingsStore.Read(path).Properties.MachineID.Value);
    }

    [TestMethod]
    public void SocketCloseDestinationSnapshotCannotPersistOverCachedLocalIdentity()
    {
        var settings = Load();
        Assert.AreEqual(LocalId, settings.MachineId); // Match a running MWB process.
        settings.DesMachineID = PeerId;             // Match SocketStuff.Close.
        settings.SaveSettingsSynchronously();

        Assert.AreEqual(LocalId, settings.MachineId);
        Assert.AreEqual(LocalId, Load().MachineId);
    }

    [TestMethod]
    public void NoneAndAllDestinationsNeverBecomePersistentMachineIdentity()
    {
        foreach (uint destination in new[] { (uint)ID.NONE, (uint)ID.ALL })
        {
            var settings = Load();
            settings.DesMachineID = destination;
            Assert.AreEqual(destination, settings.DesMachineID);
            settings.SaveSettingsSynchronously();
            Assert.AreEqual(LocalId, PortableSettingsStore.Read(path).Properties.MachineID.Value);
        }
    }

    [TestMethod]
    public void DestinationDefaultsToLocalIdentityAndIsNotRestoredAsPeerAfterRestart()
    {
        var settings = Load();
        Assert.AreEqual((uint)LocalId, settings.DesMachineID);
        settings.DesMachineID = PeerId;
        settings.SaveSettingsSynchronously();

        var restarted = Load();
        Assert.AreEqual(LocalId, restarted.MachineId);
        Assert.AreEqual((uint)LocalId, restarted.DesMachineID);
    }

    [TestMethod]
    public void SecureProfileExportAfterSocketCloseKeepsThisComputerIdentity()
    {
        var settings = Load();
        Assert.AreEqual(LocalId, settings.MachineId);
        settings.DesMachineID = PeerId;
        settings.SaveSettingsSynchronously();

        var profile = SecureDesktopProfile.FromPreferences(PortableSettingsStore.Read(path), "LOCAL-PC");
        Assert.AreEqual(LocalId, profile.MachineId);
        Assert.AreEqual($"LOCAL-PC:{LocalId},PEER-PC:{PeerId},:,:", PortableSettingsStore.Read(path).Properties.MachinePool.Value);
    }

    [TestMethod]
    public void ExplicitLocalIdentityChangeDoesNotOverwriteRememberedPeerDestination()
    {
        var settings = Load();
        settings.PauseInstantSaving = true;
        settings.DesMachineID = PeerId;
        settings.MachineId = LocalId + 1;
        Assert.AreEqual(PeerId, settings.DesMachineID);
        Assert.AreEqual(LocalId + 1, settings.MachineId);
        settings.SaveSettingsSynchronously();
        Assert.AreEqual(LocalId + 1, Load().MachineId);
    }
}
