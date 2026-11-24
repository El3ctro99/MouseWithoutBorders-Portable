// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PortableElevationTests
{
    private static readonly string Executable = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.Tests", "one", "MouseWithoutBorders.exe");
    private static readonly string OtherExecutable = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.Tests", "two", "MouseWithoutBorders.exe");

    [TestMethod]
    public void FreshLocalChoiceDefaultsToNormalAndDoesNotCreateValues()
    {
        WithRegistryKey(key =>
        {
            Assert.IsFalse(PortableElevation.IsEnabled(key, Executable));
            Assert.AreEqual(0, key.ValueCount);
        });
    }

    [TestMethod]
    public void ChoiceIsStoredAsExactDwordOneForOnlyOneExecutablePath()
    {
        WithRegistryKey(key =>
        {
            PortableElevation.SetEnabled(key, Executable, enabled: true);
            Assert.IsTrue(PortableElevation.IsEnabled(key, Executable));
            Assert.IsFalse(PortableElevation.IsEnabled(key, OtherExecutable));
            Assert.AreEqual(RegistryValueKind.DWord, key.GetValueKind(PortableElevation.ValueName(Executable)));
            Assert.AreEqual(1, key.GetValue(PortableElevation.ValueName(Executable)));
            Assert.AreEqual(1, key.ValueCount);
        });
    }

    [TestMethod]
    public void EquivalentWindowsPathSpellingUsesSamePrivateHash()
    {
        string alternate = Path.Combine(Path.GetDirectoryName(Executable)!, ".", Path.GetFileName(Executable)).ToUpperInvariant();
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Executable).ToUpperInvariant())));
        Assert.AreEqual(expected, PortableElevation.ValueName(Executable));
        Assert.AreEqual(64, expected.Length);
        Assert.AreEqual(expected, PortableElevation.ValueName(alternate));
        WithRegistryKey(key =>
        {
            PortableElevation.SetEnabled(key, Executable, enabled: true);
            Assert.IsTrue(PortableElevation.IsEnabled(key, alternate));
        });
    }

    [TestMethod]
    public void DisablingRemovesOnlyThisChoiceAndPreservesOtherCopiesAndValues()
    {
        WithRegistryKey(key =>
        {
            PortableElevation.SetEnabled(key, Executable, enabled: true);
            PortableElevation.SetEnabled(key, OtherExecutable, enabled: true);
            key.SetValue("Unrelated", "keep", RegistryValueKind.String);
            PortableElevation.SetEnabled(key, Executable, enabled: false);
            Assert.IsFalse(PortableElevation.IsEnabled(key, Executable));
            Assert.IsTrue(PortableElevation.IsEnabled(key, OtherExecutable));
            Assert.AreEqual("keep", key.GetValue("Unrelated"));
            Assert.AreEqual(2, key.ValueCount);
            PortableElevation.SetEnabled(key, Executable, enabled: false);
            Assert.AreEqual(2, key.ValueCount, "Disabling an already absent entry is idempotent.");
        });
    }

    [TestMethod]
    public void OnlyExactDwordOneEnablesAdministratorLaunch()
    {
        WithRegistryKey(key =>
        {
            string name = PortableElevation.ValueName(Executable);
            foreach (var item in new (object Value, RegistryValueKind Kind)[]
            {
                ("1", RegistryValueKind.String), ("1", RegistryValueKind.ExpandString),
                (new byte[] { 1 }, RegistryValueKind.Binary), (1L, RegistryValueKind.QWord),
                (0, RegistryValueKind.DWord), (2, RegistryValueKind.DWord), (-1, RegistryValueKind.DWord),
            })
            {
                key.SetValue(name, item.Value, item.Kind);
                Assert.IsFalse(PortableElevation.IsEnabled(key, Executable), item.Kind + " / " + item.Value);
            }
        });
    }

    [TestMethod]
    public void RelativeOrEmptyPathCannotCreateAChoice()
    {
        WithRegistryKey(key =>
        {
            foreach (string path in new[] { "", " ", "MouseWithoutBorders.exe", @"folder\MouseWithoutBorders.exe" })
                Assert.ThrowsException<ArgumentException>(() => PortableElevation.SetEnabled(key, path, enabled: true));
            Assert.AreEqual(0, key.ValueCount);
        });
    }

    [TestMethod]
    public void OnlyEnabledOrdinaryMediumUserLaunchCanRequestElevation()
    {
        string[] ordinary = { "app.exe" };
        Assert.IsTrue(PortableElevation.ShouldRelaunch(ordinary, enabled: true, elevated: false, system: false));
        Assert.IsFalse(PortableElevation.ShouldRelaunch(ordinary, enabled: false, elevated: false, system: false));
        Assert.IsFalse(PortableElevation.ShouldRelaunch(ordinary, enabled: true, elevated: true, system: false));
        Assert.IsFalse(PortableElevation.ShouldRelaunch(ordinary, enabled: true, elevated: false, system: true));
        Assert.IsFalse(PortableElevation.ShouldRelaunch(null, enabled: true, elevated: false, system: false));
        Assert.IsFalse(PortableElevation.ShouldRelaunch(Array.Empty<string>(), enabled: true, elevated: false, system: false));
    }

    [TestMethod]
    public void EveryArgumentBearingHelperOrUnknownModeIsExcluded()
    {
        foreach (string marker in new[]
        {
            PortableApplication.ClipboardHelperArgument, PortableUpdateApply.ApplyArgument,
            PortableInstallLifecycle.LaunchArgument, "--mwb-secure-desktop-agent", "--mwb-export-secure-profile",
            "UseService", "winlogon", "default", "--unknown", "",
        })
        {
            string[] args = { "app.exe", marker };
            Assert.IsFalse(PortableElevation.ShouldRelaunch(args, enabled: true, elevated: false, system: false), marker);
            Assert.IsFalse(PortableElevation.TryRelaunchElevated(args), "Excluded modes must not read settings or trigger UAC: " + marker);
        }
    }

    [TestMethod]
    public void LaunchRequestsOnlyCurrentExecutableWithoutForwardedArguments()
    {
        ProcessStartInfo info = PortableElevation.CreateCurrentLaunchStartInfo();
        Assert.AreEqual(PortableApplication.CurrentExecutablePath, info.FileName);
        Assert.AreEqual(Path.GetDirectoryName(PortableApplication.CurrentExecutablePath), info.WorkingDirectory);
        Assert.IsTrue(info.UseShellExecute);
        Assert.AreEqual("runas", info.Verb);
        Assert.AreEqual(string.Empty, info.Arguments);
        Assert.AreEqual(0, info.ArgumentList.Count);
    }

    [TestMethod]
    public void ConfirmedChildCreationExitsOriginalWithoutFailureMessage()
    {
        int starts = 0;
        var reports = new List<string>();
        Assert.IsTrue(PortableElevation.TryStartElevation(info =>
        {
            starts++;
            Assert.AreEqual(PortableApplication.CurrentExecutablePath, info.FileName);
            return true;
        }, reports.Add));
        Assert.AreEqual(1, starts);
        Assert.AreEqual(0, reports.Count);
    }

    [TestMethod]
    public void MissingProcessConfirmationKeepsNormalCopyAndExplainsIt()
    {
        var reports = new List<string>();
        Assert.IsFalse(PortableElevation.TryStartElevation(_ => false, reports.Add));
        Assert.AreEqual(1, reports.Count);
        StringAssert.Contains(reports[0], "continue without administrator");
    }

    [TestMethod]
    public void CancelledUacKeepsNormalCopyAndDoesNotClearTheChoice()
    {
        var reports = new List<string>();
        Assert.IsFalse(PortableElevation.TryStartElevation(_ => throw new Win32Exception(1223), reports.Add));
        Assert.AreEqual(1, reports.Count);
        StringAssert.Contains(reports[0], "cancelled");
        StringAssert.Contains(reports[0], "continue without administrator");
        StringAssert.Contains(reports[0], "remains enabled");
    }

    [TestMethod]
    public void LaunchErrorsKeepNormalCopyWithoutLeakingExceptionDetails()
    {
        foreach (Exception error in new Exception[] { new Win32Exception(5), new IOException("private-pairing-data"), new InvalidOperationException("private-pairing-data") })
        {
            var reports = new List<string>();
            Assert.IsFalse(PortableElevation.TryStartElevation(_ => throw error, reports.Add));
            Assert.AreEqual(1, reports.Count);
            StringAssert.Contains(reports[0], "continue without administrator");
            Assert.IsFalse(reports[0].Contains("private-pairing-data", StringComparison.Ordinal));
        }
    }

    private static void WithRegistryKey(Action<RegistryKey> test)
    {
        string path = @"Software\MouseWithoutBorders.Tests\AdministratorLaunch-" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
                ?? throw new IOException("Windows did not create the isolated test registry key.");
            test(key);
        }
        finally
        {
            // This key is a fixed test prefix plus a freshly generated GUID and
            // tests never create subkeys. Do not recursively remove registry data.
            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }
}
