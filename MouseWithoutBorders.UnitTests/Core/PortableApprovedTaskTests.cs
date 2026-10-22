// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

/// <summary>Pure trust-policy tests. No files, registry entries or tasks are created.</summary>
[TestClass]
public sealed class PortableApprovedTaskTests
{
    [DataTestMethod]
    [DataRow(false, 0x12)]
    [DataRow(true, 0x16)]
    public void RegistrationPreservesTheExplicitPrincipalAcl(bool existed, int expected)
    {
        Assert.AreEqual(expected, PortableApprovedTask.RegistrationFlags(existed));
    }

    [TestMethod]
    public void MissingTaskHresultsAreRecognizedAfterComMapsThemToManagedExceptions()
    {
        foreach (int code in new[] { unchecked((int)0x80070002), unchecked((int)0x80070003) })
        {
            Exception mapped = System.Runtime.InteropServices.Marshal.GetExceptionForHR(code)!;
            Assert.IsTrue(mapped is FileNotFoundException or DirectoryNotFoundException);
            Assert.IsTrue(PortableApprovedTask.IsMissingTask(mapped));
            Assert.IsTrue(PortableApprovedTask.IsMissingTask(new System.Runtime.InteropServices.COMException("missing", code)));
        }
        Assert.IsFalse(PortableApprovedTask.IsMissingTask(new UnauthorizedAccessException()));
        Assert.IsFalse(PortableApprovedTask.IsMissingTask(new InvalidOperationException()));
        Assert.IsFalse(PortableApprovedTask.IsMissingTask(new System.Runtime.InteropServices.COMException("access denied", unchecked((int)0x80070005))));
    }

    private const string PolicyUser = "S-1-5-21-101-202-303-1001";
    private const string OtherUser = "S-1-5-21-101-202-303-1002";
    private const string PolicyRoot = @"C:\Program Files\Mouse Without Borders Approved Launch\S-1-5-21-101-202-303-1001";

    private static string CurrentSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }

    private static string Rules => "(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;" + CurrentSid + ")(A;;RC;;;OW)";
    private static RawSecurityDescriptor Descriptor(string rules, string owner = "BA", bool protect = true)
        => new("O:" + owner + "G:BAD:" + (protect ? "P" : string.Empty) + rules);

    [DataTestMethod]
    [DataRow("BA")]
    [DataRow("SY")]
    public void ImmutablePayloadAcceptsOnlyProtectedAdministratorOwnedReadExecutePolicy(string owner)
    {
        PortableApprovedTask.ValidateDescriptor(Descriptor(Rules, owner), requireUserRead: true, requireOwnerRights: true);
    }

    [TestMethod]
    public void NormalUserOwnershipCannotUseImplicitOwnerPermissionsToWeakenThePayload()
    {
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(
            Descriptor(Rules, CurrentSid), true, true));
    }

    [DataTestMethod]
    [DataRow("(A;;GW;;;BU)")]
    [DataRow("(A;;GA;;;WD)")]
    [DataRow("(A;;WD;;;AU)")]
    [DataRow("(A;;WO;;;AU)")]
    [DataRow("(A;;SD;;;BU)")]
    [DataRow("(A;;0x40;;;BU)")]
    public void AnyUntrustedWriteDeleteOrAclGrantIsRejected(string additionalRule)
    {
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(
            Descriptor(Rules + additionalRule), true, true));
    }

    [TestMethod]
    public void OwnUserWriteDacIsRejectedEvenAlongsideTheNormalReadExecuteRule()
    {
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(
            Descriptor(Rules + "(A;;WD;;;" + CurrentSid + ")"), true, true));
    }

    [TestMethod]
    public void ReadOnlyOtherIdentitiesDoNotBecomeWriters()
    {
        PortableApprovedTask.ValidateDescriptor(Descriptor(Rules + "(A;;FRFX;;;BU)"), true, true);
    }

    [TestMethod]
    public void RuntimePolicyRequiresOwnerRightsReadControlWithoutImplicitWriteDac()
    {
        string missing = Rules.Replace("(A;;RC;;;OW)", string.Empty, StringComparison.Ordinal);
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(Descriptor(missing), true, true));
        string dangerous = Rules.Replace("(A;;RC;;;OW)", "(A;;RCWD;;;OW)", StringComparison.Ordinal);
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(Descriptor(dangerous), true, true));
        // Task objects have a distinct policy and do not need the filesystem's
        // inherited OWNER RIGHTS rule.
        PortableApprovedTask.ValidateDescriptor(Descriptor(missing), true, false);
    }

    [TestMethod]
    public void UnprotectedAndNullDaclsCannotPassAsRestrictedPayloads()
    {
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(Descriptor(Rules, protect: false), true, true));
        var nullDacl = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            new SecurityIdentifier("S-1-5-32-544"), new SecurityIdentifier("S-1-5-32-544"), null, null);
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(nullDacl, true, true));
    }

    [TestMethod]
    public void BothTrustedAdministratorsAndSystemNeedFullControl()
    {
        foreach (string omitted in new[] { "(A;;FA;;;SY)", "(A;;FA;;;BA)" })
            Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.ValidateDescriptor(
                Descriptor(Rules.Replace(omitted, string.Empty, StringComparison.Ordinal)), true, true));
    }

    [DataTestMethod]
    [DataRow(@"MouseWithoutBorders.exe")]
    [DataRow(@".\MouseWithoutBorders.exe")]
    [DataRow(@"C:MouseWithoutBorders.exe")]
    [DataRow(@"\\server\share\MouseWithoutBorders.exe")]
    [DataRow(@"\\?\C:\app\MouseWithoutBorders.exe")]
    [DataRow(@"C:\app\MouseWithoutBorders.exe:payload.exe")]
    [DataRow(@"C:\app\MouseWithoutBorders.exe --unsafe")]
    [DataRow("\"C:\\app\\MouseWithoutBorders.exe\" --unsafe")]
    [DataRow("C:\\app\\MouseWithoutBorders.exe\r\nanything")]
    public void SourceRecordRejectsCommandsRemotePathsAndAlternateStreams(string source)
    {
        Assert.ThrowsException<SecurityException>(() => PortableApprovedTask.CanonicalSource(source));
    }

    [TestMethod]
    public void SourceRecordCanonicalizesAnAbsoluteLocalExeWithoutInterpretingArguments()
    {
        string drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        string expected = Path.Combine(drive, "MWB policy test", "MouseWithoutBorders.exe");
        Assert.AreEqual(expected, PortableApprovedTask.CanonicalSource(Path.Combine(drive, "MWB policy test", ".", "MouseWithoutBorders.exe")));
    }

    private static PortableApprovedTask.TaskPolicy Policy() => new(PolicyUser, 3, 1, 0, 1, 0,
        Path.Combine(PolicyRoot, PortableApprovedTask.LauncherFileName), string.Empty, PolicyRoot, true, "<Task />", true);

    private static bool Matches(PortableApprovedTask.TaskPolicy policy, string user = PolicyUser)
        => PortableApprovedTask.MatchesTaskPolicy(policy, user, PolicyRoot);

    [TestMethod]
    public void TaskPolicyAllowsOnlyTheSameUsersFixedOnDemandNativeAction()
    {
        Assert.IsTrue(Matches(Policy()));
        Assert.IsTrue(Matches(Policy() with { ActionPath = Policy().ActionPath.ToUpperInvariant() }));
    }

    [TestMethod]
    public void TaskPolicyRejectsWrongPrincipalAndSystemEvenIfTheExpectedSidWereWrong()
    {
        Assert.IsFalse(Matches(Policy() with { UserSid = OtherUser }));
        foreach (string system in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20" })
            Assert.IsFalse(Matches(Policy() with { UserSid = system }, system));
    }

    [TestMethod]
    public void TaskPolicyRejectsStoredCredentialsServiceLogonsAndLimitedRunLevel()
    {
        foreach (int logon in new[] { 0, 1, 2, 4, 5, 6 }) Assert.IsFalse(Matches(Policy() with { LogonType = logon }));
        Assert.IsFalse(Matches(Policy() with { RunLevel = 0 }));
    }

    [TestMethod]
    public void TaskPolicyRejectsTriggersExtraActionsAndCallerParameters()
    {
        Assert.IsFalse(Matches(Policy() with { TriggerCount = 1 }));
        Assert.IsFalse(Matches(Policy() with { ActionCount = 2 }));
        Assert.IsFalse(Matches(Policy() with { ActionType = 5 }));
        Assert.IsFalse(Matches(Policy() with { Arguments = "--mwb-approved-main" }));
        Assert.IsFalse(Matches(Policy() with { Arguments = " " }));
        Assert.IsFalse(Matches(Policy() with { Xml = "<Arguments>$(Arg0)</Arguments>" }));
    }

    [TestMethod]
    public void TaskPolicyRejectsOtherExecutablesLocationsAndUnavailableDemandStart()
    {
        Assert.IsFalse(Matches(Policy() with { ActionPath = Path.Combine(PolicyRoot, PortableApprovedTask.MainFileName) }));
        Assert.IsFalse(Matches(Policy() with { WorkingDirectory = @"C:\Users\Public" }));
        Assert.IsFalse(Matches(Policy() with { AllowDemandStart = false }));
        Assert.IsFalse(Matches(Policy() with { Enabled = false }));
        Assert.IsTrue(PortableApprovedTask.MatchesTaskPolicy(Policy() with { Enabled = false }, PolicyUser, PolicyRoot, requireEnabled: false),
            "A disabled but otherwise matching task can be inspected and removed safely.");
    }
}
