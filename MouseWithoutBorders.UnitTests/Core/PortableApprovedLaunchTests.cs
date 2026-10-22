// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PortableApprovedLaunchTests
{
    [TestMethod]
    public void TaskRouteRequiresOrdinaryEnabledUnelevatedUserLaunch()
    {
        string[] args = { "app.exe" };
        Assert.IsTrue(PortableApprovedLaunch.ShouldUseTask(args, true, false, false));
        Assert.IsFalse(PortableApprovedLaunch.ShouldUseTask(args, false, false, false));
        Assert.IsFalse(PortableApprovedLaunch.ShouldUseTask(args, true, true, false));
        Assert.IsFalse(PortableApprovedLaunch.ShouldUseTask(args, true, false, true));
        Assert.IsFalse(PortableApprovedLaunch.ShouldUseTask(null, true, false, false));
        Assert.IsFalse(PortableApprovedLaunch.ShouldUseTask(Array.Empty<string>(), true, false, false));
    }

    [DataTestMethod]
    [DataRow(PortableApplication.ClipboardHelperArgument)]
    [DataRow(PortableApprovedLaunch.MainArgument)]
    [DataRow(PortableApprovedLaunch.SetupArgument)]
    [DataRow("--mwb-update-apply")]
    [DataRow("--mwb-secure-desktop-agent")]
    [DataRow("--unknown")]
    [DataRow("")]
    public void ArgumentBearingModesCannotRequestApprovedTask(string marker)
    {
        var args = new[] { "app.exe", marker };
        Assert.IsFalse(PortableApprovedLaunch.ShouldUseTask(args, true, false, false));
        Assert.IsFalse(PortableApprovedLaunch.TryRelaunch(args), "Excluded modes do not read task state or launch a process.");
    }

    [TestMethod]
    public void SetupAcceptsOnlyTwoOperationsForTheExactExpectedAccount()
    {
        const string sid = "S-1-5-21-10-20-30-1001";
        Assert.IsTrue(PortableApprovedLaunch.IsSetupShape(new[] { "app", PortableApprovedLaunch.SetupArgument, "enable", sid }, sid));
        Assert.IsTrue(PortableApprovedLaunch.IsSetupShape(new[] { "app", PortableApprovedLaunch.SetupArgument, "disable", sid }, sid));
        foreach (string operation in new[] { "run", "Enable", "", "enable extra" })
            Assert.IsFalse(PortableApprovedLaunch.IsSetupShape(new[] { "app", PortableApprovedLaunch.SetupArgument, operation, sid }, sid));
        Assert.IsFalse(PortableApprovedLaunch.IsSetupShape(new[] { "app", PortableApprovedLaunch.SetupArgument, "enable", "S-1-5-18" }, sid));
        Assert.IsFalse(PortableApprovedLaunch.IsSetupShape(new[] { "app", PortableApprovedLaunch.SetupArgument, "enable", sid, "extra" }, sid));
        Assert.IsFalse(PortableApprovedLaunch.IsSetupShape(null, sid));
        Assert.IsFalse(PortableApprovedLaunch.IsSetupShape(new[] { "app", PortableApprovedLaunch.SetupArgument, "enable", sid }, null));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SetupLaunchAlwaysUsesTheCurrentImageAndFixedOperation(bool enable)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        var info = PortableApprovedLaunch.CreateSetupStartInfo(enable, sid);
        Assert.AreEqual(PortableApplication.CurrentExecutablePath, info.FileName);
        Assert.AreEqual(Path.GetDirectoryName(info.FileName), info.WorkingDirectory);
        Assert.IsTrue(info.UseShellExecute);
        Assert.AreEqual("runas", info.Verb);
        CollectionAssert.AreEqual(new[] { PortableApprovedLaunch.SetupArgument, enable ? "enable" : "disable", sid }, info.ArgumentList.ToArray());
        Assert.AreEqual(string.Empty, info.Arguments);
    }

    [TestMethod]
    public void OrdinarySettingsRemainBesideTheActualExecutable()
    {
        Assert.IsFalse(PortableApprovedLaunch.IsActive);
        Assert.AreEqual(PortableApplication.CurrentExecutablePath, PortableApprovedLaunch.PreferenceExecutablePath);
        Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, PortableApplication.SettingsFileName), PortableApplication.CurrentSettingsPath);
    }

    [TestMethod]
    public void NativeBootstrapIsEmbeddedAndIsAnX64PeRatherThanAManagedLauncher()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            Assert.Inconclusive("The approved native launcher is currently provided for x64 builds.");
        Assert.IsTrue(PortableApprovedLaunch.IsSupported);
        using var stream = typeof(PortableApprovedLaunch).Assembly.GetManifestResourceStream("MouseWithoutBorders.ApprovedLauncher.exe");
        Assert.IsNotNull(stream, "Build the protected native launcher before publishing x64.");
        string? expected = Environment.GetEnvironmentVariable("MWB_EXPECTED_APPROVED_NATIVE_HASH");
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.AreEqual(expected, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)),
                "The app must embed the exact reviewed native build.");
            stream.Position = 0;
        }
        using var reader = new BinaryReader(stream);
        Assert.AreEqual((ushort)0x5A4D, reader.ReadUInt16());
        stream.Position = 0x3C;
        int pe = reader.ReadInt32();
        stream.Position = pe;
        Assert.AreEqual(0x00004550U, reader.ReadUInt32());
        Assert.AreEqual((ushort)0x8664, reader.ReadUInt16());
        stream.Position = pe + 24;
        Assert.AreEqual((ushort)0x20B, reader.ReadUInt16());
        stream.Position = pe + 24 + 68;
        Assert.AreEqual((ushort)2, reader.ReadUInt16(), "The native launcher must use the Windows GUI subsystem.");
        ushort characteristics = reader.ReadUInt16();
        Assert.AreEqual(0x160, characteristics & 0x160, "NX, ASLR and high entropy address space must remain enabled.");
        stream.Position = pe + 24 + 112 + 14 * 8;
        Assert.AreEqual(0U, reader.ReadUInt32(), "The bootstrap must run before any CLR/environment hook.");
        Assert.AreEqual(0U, reader.ReadUInt32());
    }
}
