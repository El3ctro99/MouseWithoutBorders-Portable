// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
[DoNotParallelize]
public sealed class SecureDesktopDiscoveryTests
{
    private static SecureDesktopProfile Profile() => new()
    {
        SecurityKey = "test-only-shared-key",
        LocalMachineName = "LOCAL-PC",
        MachineId = 12345,
        // Deliberately put the peer before this PC to detect discovery-order rewrites.
        MachineMatrix = new[] { "PEER-PC", "LOCAL-PC", "THIRD-PC", "" },
        MatrixOneRow = false,
        TcpPort = 15100,
        IpMappings = string.Empty,
        EasyMouse = 1,
    };

    [TestMethod]
    public void ProtectedProfileSeedsReviewedNamesWithoutInventingPeerIds()
    {
        var profile = Profile();
        var settings = profile.CreateInputOnlySettings();
        var info = MachinePoolHelpers.LoadMachineInfoFromMachinePoolStringSetting(settings.Properties.MachinePool.Value);
        CollectionAssert.AreEqual(profile.MachineMatrix, info.Select(peer => peer.Name).ToArray());
        Assert.AreEqual(ID.NONE, info[0].Id);
        Assert.AreEqual((ID)12345, info[1].Id);
        Assert.AreEqual(ID.NONE, info[2].Id);
        Assert.AreEqual(ID.NONE, info[3].Id);
        CollectionAssert.AreEqual(profile.MachineMatrix, settings.Properties.MachineMatrixString);
    }

    [TestMethod]
    public void FirstAuthenticatedPeerDoesNotRewriteReviewedMatrixOrCreateSockets()
    {
        WithProtectedRuntime(() =>
        {
            // Simulate an empty/lost discovery cache, including an older installed
            // profile. The runtime boundary must protect the layout independently
            // of the new profile's startup seeding.
            MachineStuff.MachinePool.Clear();
            MachineStuff.MachinePool.Initialize(new[] { "LOCAL-PC" });
            _ = MachineStuff.MachinePool.TryUpdateMachineID("LOCAL-PC", (ID)12345, false);
            string before = Setting.Values.MachineMatrixString;
            string learned = MachineStuff.AddToMachinePool(new DATA
            {
                Type = PackageType.Hello, MachineName = "PEER-PC", Src = (ID)67890,
            }, secureDesktopAgent: true);
            Assert.AreEqual("PEER-PC", learned);
            Assert.AreEqual((ID)67890, MachineStuff.MachinePool.ResolveID("PEER-PC"));
            Assert.AreEqual((ID)12345, MachineStuff.MachinePool.ResolveID("LOCAL-PC"));
            CollectionAssert.AreEqual(Profile().MachineMatrix, MachineStuff.MachineMatrix);
            Assert.AreEqual(before, Setting.Values.MachineMatrixString);
            Assert.IsFalse(Setting.Values.MatrixOneRow);
            Assert.IsNull(Common.Sk, "Discovery must not enter the normal application's socket-recreation flow.");
        });
    }

    [TestMethod]
    public void AuthenticatedReconnectRefreshesRuntimeIdWithoutChangingReviewedSettings()
    {
        WithProtectedRuntime(() =>
        {
            string before = Setting.Values.MachineMatrixString;
            foreach (int id in new[] { 67890, 98765 })
            {
                _ = MachineStuff.AddToMachinePool(new DATA
                {
                    Type = PackageType.Heartbeat, MachineName = "PEER-PC", Src = (ID)id,
                }, secureDesktopAgent: true);
                Assert.AreEqual((ID)id, MachineStuff.MachinePool.ResolveID("PEER-PC"));
                Assert.AreEqual(before, Setting.Values.MachineMatrixString);
                CollectionAssert.AreEqual(Profile().MachineMatrix, MachineStuff.MachineMatrix);
                Assert.IsNull(Common.Sk);
            }
        });
    }

    [TestMethod]
    public void LearningAnAdditionalAuthenticatedPeerDoesNotAddItToReviewedLayout()
    {
        WithProtectedRuntime(() =>
        {
            _ = MachineStuff.AddToMachinePool(new DATA
            {
                Type = PackageType.Hello, MachineName = "FOURTH-PC", Src = (ID)34567,
            }, secureDesktopAgent: true);
            Assert.AreEqual((ID)34567, MachineStuff.MachinePool.ResolveID("FOURTH-PC"));
            CollectionAssert.AreEqual(Profile().MachineMatrix, MachineStuff.MachineMatrix);
            Assert.IsFalse(Setting.Values.MachineMatrixString.Contains("FOURTH-PC", StringComparison.Ordinal));
            Assert.IsNull(Common.Sk);
        });
    }

    [TestMethod]
    public void ProtectedInputObjectNeverRegistersLocalCaptureHooksOnStartOrRestart()
    {
        var hook = new InputHook(secureDesktopAgent: true);
        try
        {
            // A hook-free receiver has no Windows callback through which it can
            // swallow physical mouse/keyboard input. Receiving SendInput and
            // relative boundary responses do not depend on hook registration.
            Assert.IsFalse(hook.HasInstalledCaptureHooks);
            hook.Stop();
            hook.Start();
            Assert.IsFalse(hook.HasInstalledCaptureHooks);
        }
        finally { hook.Stop(); }
    }

    private static void WithProtectedRuntime(Action action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MwbSecureDiscoveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var originalSettings = Setting.Values;
        var originalMatrix = MachineStuff.MachineMatrix.ToArray();
        var originalPool = MachineStuff.MachinePool.ListAllMachines();
        var originalSockets = Common.Sk;
        var originalForm = Common.MainForm;
        try
        {
            string path = Path.Combine(directory, "preferences.json");
            var settings = Profile().CreateInputOnlySettings();
            File.WriteAllText(path, JsonSerializer.Serialize(settings, SettingsUtils.SerializerOptions));
            Setting.Values = new MouseWithoutBorders.Class.Settings(new SettingsUtils(path), watchChanges: false)
            {
                PauseInstantSaving = true,
            };
            Common.Sk = null;
            Common.MainForm = null;
            MachineStuff.MachineMatrix = Profile().MachineMatrix.ToArray();
            MachineStuff.MachinePool.Initialize(MachinePoolHelpers.LoadMachineInfoFromMachinePoolStringSetting(Setting.Values.MachinePoolString));
            action();
        }
        finally
        {
            MachineStuff.MachineMatrix = originalMatrix;
            MachineStuff.MachinePool.Initialize(originalPool);
            Setting.Values = originalSettings;
            Common.Sk = originalSockets;
            Common.MainForm = originalForm;
            Directory.Delete(directory, recursive: true);
        }
    }
}
