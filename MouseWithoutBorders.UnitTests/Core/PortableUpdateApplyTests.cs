// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class PortableUpdateApplyTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static PortableUpdateApply.UpdatePlan Plan() => new(1, Id, 1234, 1234567, 1,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Portable", "MouseWithoutBorders.exe"),
        Hash("old-exe"), Hash("new-exe"));

    [TestMethod]
    public void HelperMarkerRequiresExactFirstArgument()
    {
        Assert.IsTrue(PortableUpdateApply.IsApplyInvocation(new[] { "app.exe", PortableUpdateApply.ApplyArgument, "plan.json" }));
        Assert.IsFalse(PortableUpdateApply.IsApplyInvocation(new[] { "app.exe", "--MWB-APPLY-UPDATE", "plan.json" }));
        Assert.IsFalse(PortableUpdateApply.IsApplyInvocation(new[] { "app.exe", "other", PortableUpdateApply.ApplyArgument }));
        Assert.IsFalse(PortableUpdateApply.IsApplyInvocation(new[] { "app.exe" }));
    }

    [TestMethod]
    public void PlanRoundTripsAndBindsExactParentIdentity()
    {
        var plan = Plan();
        Assert.AreEqual(plan, PortableUpdateApply.ParsePlan(JsonSerializer.SerializeToUtf8Bytes(plan)));
        Assert.IsTrue(PortableUpdateApply.MatchesParent(plan, 1234, 1234567, 1, plan.TargetPath));
        Assert.IsFalse(PortableUpdateApply.MatchesParent(plan, 1235, 1234567, 1, plan.TargetPath));
        Assert.IsFalse(PortableUpdateApply.MatchesParent(plan, 1234, 1234568, 1, plan.TargetPath));
        Assert.IsFalse(PortableUpdateApply.MatchesParent(plan, 1234, 1234567, 2, plan.TargetPath));
        Assert.IsFalse(PortableUpdateApply.MatchesParent(plan, 1234, 1234567, 1, plan.TargetPath + ".other.exe"));
    }

    [TestMethod]
    public void UnknownDuplicateOrMissingPlanFieldsAreRejected()
    {
        string json = JsonSerializer.Serialize(Plan());
        Reject(json[..^1] + ",\"Command\":\"arbitrary.exe\"}");
        Reject(json[..^1] + ",\"ParentPid\":1234}");
        Reject(json.Replace("\"Version\":1,", string.Empty, StringComparison.Ordinal));
        Reject("[]");
        Reject("{}");
        Reject("not json");
    }

    [TestMethod]
    public void InvalidPlanValuesCannotSelectAnotherExecutable()
    {
        var plan = Plan();
        foreach (var invalid in new[]
        {
            plan with { Version = 2 }, plan with { Id = "../escape" }, plan with { ParentPid = 0 },
            plan with { ParentStartedTicks = 0 }, plan with { SessionId = 0 },
            plan with { OldSha256 = new string('z', 64) }, plan with { NewSha256 = "1234" },
            plan with { TargetPath = "MouseWithoutBorders.exe" },
            plan with { TargetPath = Path.ChangeExtension(plan.TargetPath, ".dll") },
        })
            Reject(JsonSerializer.Serialize(invalid));
    }

    [TestMethod]
    public void OversizedPlanIsRejectedBeforeParsing()
    {
        Assert.ThrowsException<InvalidDataException>(() => PortableUpdateApply.ParsePlan(new byte[16385]));
        Assert.ThrowsException<InvalidDataException>(() => PortableUpdateApply.ParsePlan(Array.Empty<byte>()));
    }

    [TestMethod]
    public void ProtectedAndOutsideProfileTargetsAreRejected()
    {
        foreach (string target in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mouse Without Borders Secure Desktop", "MouseWithoutBorders.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MouseWithoutBorders.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MouseWithoutBorders.exe"),
            @"\\server\share\MouseWithoutBorders.exe",
            @"C:\MouseWithoutBorders.exe",
        })
            Assert.ThrowsException<SecurityException>(() => PortableUpdateApply.ValidateNormalTarget(target), target);
    }

    [TestMethod]
    public void AlternateStreamsAndDevicePathsAreRejected()
    {
        foreach (string target in new[]
        {
            Plan().TargetPath + ":stream",
            @"\\?\C:\Users\test\MouseWithoutBorders.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "folder.", "MouseWithoutBorders.exe"),
        })
            Assert.ThrowsException<SecurityException>(() => PortableUpdateApply.ValidateNormalTarget(target));
    }

    [TestMethod]
    public void ExeOnlySuccessKeepsPreferencesAndCleansBackup()
    {
        WithFixture((directory, target, payload, preferences) =>
        {
            byte[] before = File.ReadAllBytes(preferences);
            int validations = 0;
            var launches = new List<string>();
            PortableUpdateApply.ApplyTransaction(target, payload, Hash("old-exe"), Hash("new-exe"), Id,
                () => validations++, path => { launches.Add(File.ReadAllText(path)); return true; });
            Assert.AreEqual("new-exe", File.ReadAllText(target));
            Assert.AreEqual("new-exe", File.ReadAllText(payload));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(preferences));
            CollectionAssert.AreEqual(new[] { "new-exe" }, launches);
            Assert.AreEqual(2, validations);
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.bak").Length);
            Assert.AreEqual(0, Directory.GetFiles(directory, ".mwb-update-*.tmp").Length);
        });
    }

    [TestMethod]
    public void WrongDownloadedHashKeepsOldExeAndPreferences()
    {
        WithFixture((_, target, payload, preferences) =>
        {
            byte[] before = File.ReadAllBytes(preferences);
            var launches = new List<string>();
            File.WriteAllText(payload, "tampered-exe");
            Assert.ThrowsException<InvalidDataException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, path => { launches.Add(File.ReadAllText(path)); return true; }));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            CollectionAssert.AreEqual(new[] { "old-exe" }, launches);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(preferences));
        });
    }

    [TestMethod]
    public void ChangedOldExeIsNeverReplacedOrLaunched()
    {
        WithFixture((_, target, payload, _) =>
        {
            File.WriteAllText(target, "changed-old-exe");
            int launches = 0;
            Assert.ThrowsException<InvalidDataException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, _ => { launches++; return true; }));
            Assert.AreEqual("changed-old-exe", File.ReadAllText(target));
            Assert.AreEqual(0, launches);
        });
    }

    [TestMethod]
    public void PayloadTamperDuringCopyWindowIsRechecked()
    {
        WithFixture((directory, target, payload, _) =>
        {
            int checks = 0;
            Assert.ThrowsException<InvalidDataException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id,
                () => { if (++checks == 2) File.WriteAllText(payload, "changed-after-copy"); }, _ => true));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            Assert.AreEqual(0, Directory.GetFiles(directory, ".mwb-update-*.tmp").Length);
        });
    }

    [TestMethod]
    public void DestinationSiblingTamperIsRecheckedImmediatelyBeforeReplace()
    {
        WithFixture((directory, target, payload, _) =>
        {
            int checks = 0;
            Assert.ThrowsException<InvalidDataException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id,
                () => { if (++checks == 2) File.WriteAllText(Path.Combine(directory, ".mwb-update-" + Id + ".tmp"), "tampered-sibling"); }, _ => true));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
        });
    }

    [TestMethod]
    public void FailedNewStartupRestoresAndRestartsOldExe()
    {
        WithFixture((directory, target, payload, preferences) =>
        {
            byte[] before = File.ReadAllBytes(preferences);
            var launches = new List<string>();
            Assert.ThrowsException<IOException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, path =>
                {
                    string content = File.ReadAllText(path);
                    launches.Add(content);
                    return content == "old-exe";
                }));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            CollectionAssert.AreEqual(new[] { "new-exe", "old-exe" }, launches);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(preferences));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.bak").Length);
        });
    }

    [TestMethod]
    public void FailedLaunchExceptionAlsoRestoresOldExe()
    {
        WithFixture((_, target, payload, _) =>
        {
            int attempts = 0;
            Assert.ThrowsException<InvalidOperationException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, _ =>
                { if (++attempts == 1) throw new InvalidOperationException("launch failed"); return true; }));
            Assert.AreEqual(2, attempts);
            Assert.AreEqual("old-exe", File.ReadAllText(target));
        });
    }

    [TestMethod]
    public void FailedRecoveryKeepsOldBackupForManualRecovery()
    {
        WithFixture((_, target, payload, _) =>
        {
            Assert.ThrowsException<IOException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, _ => false));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            Assert.AreEqual("old-exe", File.ReadAllText(target + ".mwb-update-" + Id + ".bak"));
        });
    }

    [TestMethod]
    public void ExistingRecoveryBackupIsNotOverwritten()
    {
        WithFixture((_, target, payload, _) =>
        {
            string backup = target + ".mwb-update-" + Id + ".bak";
            File.WriteAllText(backup, "keep-existing-recovery");
            Assert.ThrowsException<IOException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, _ => true));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            Assert.AreEqual("keep-existing-recovery", File.ReadAllText(backup));
        });
    }

    [TestMethod]
    public void ExistingDestinationSiblingIsNotDeletedByFailedCopy()
    {
        WithFixture((directory, target, payload, _) =>
        {
            string sibling = Path.Combine(directory, ".mwb-update-" + Id + ".tmp");
            File.WriteAllText(sibling, "keep-existing-sibling");
            Assert.ThrowsException<IOException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, _ => true));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            Assert.AreEqual("keep-existing-sibling", File.ReadAllText(sibling));
        });
    }

    [TestMethod]
    public void CancellationImmediatelyBeforeReplaceKeepsOldExe()
    {
        WithFixture((directory, target, payload, _) =>
        {
            int abortChecks = 0;
            Assert.ThrowsException<OperationCanceledException>(() => PortableUpdateApply.ApplyTransaction(target, payload,
                Hash("old-exe"), Hash("new-exe"), Id, () => { }, _ => true,
                () => { if (++abortChecks == 2) throw new OperationCanceledException(); }));
            Assert.AreEqual("old-exe", File.ReadAllText(target));
            Assert.AreEqual(0, Directory.GetFiles(directory, ".mwb-update-*.tmp").Length);
        });
    }

    [TestMethod]
    public void ParentWaitTimeoutDoesNotAssumeExit()
    {
        long elapsed = 0;
        Assert.ThrowsException<TimeoutException>(() => PortableUpdateApply.WaitForExitOrAbort(() => false, () => false,
            () => elapsed, delay => elapsed += delay, TimeSpan.FromMilliseconds(150)));
        Assert.AreEqual(150L, elapsed);
    }

    [TestMethod]
    public void CancellationWinsEvenWhenParentExitsLater()
    {
        long elapsed = 0;
        bool aborted = false;
        bool exited = false;
        Assert.ThrowsException<OperationCanceledException>(() => PortableUpdateApply.WaitForExitOrAbort(() => exited,
            () => aborted, () => elapsed, delay => { elapsed += delay; aborted = true; exited = true; }, TimeSpan.FromSeconds(30)));
        Assert.AreEqual(50L, elapsed);
    }

    [TestMethod]
    public void ParentExitIsRecheckedAgainstCancellation()
    {
        bool aborted = false;
        Assert.ThrowsException<OperationCanceledException>(() => PortableUpdateApply.WaitForExitOrAbort(
            () => { aborted = true; return true; }, () => aborted, () => 0, _ => Assert.Fail("Already exited"), TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public void CleanupReclaimsOnlyCompletedInactiveStagesAndKeepsResultRecords()
    {
        WithCleanupFixture(root =>
        {
            string success = CompletedStage(root, Guid.NewGuid().ToString("N"));
            string failure = CompletedStage(root, Guid.NewGuid().ToString("N"), success: false);
            var before = new Dictionary<string, byte[]>
            {
                [success] = File.ReadAllBytes(Path.Combine(success, PortableUpdateApply.ResultFileName)),
                [failure] = File.ReadAllBytes(Path.Combine(failure, PortableUpdateApply.ResultFileName)),
            };
            Assert.AreEqual(2, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
            foreach (var stage in before)
            {
                CollectionAssert.AreEqual(new[] { PortableUpdateApply.ResultFileName },
                    Directory.GetFiles(stage.Key).Select(Path.GetFileName).ToArray());
                CollectionAssert.AreEqual(stage.Value, File.ReadAllBytes(Path.Combine(stage.Key, PortableUpdateApply.ResultFileName)));
            }
            Assert.AreEqual(0, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
        });
    }

    [TestMethod]
    public void CleanupPreservesMissingMalformedOversizedAndUnboundRecords()
    {
        WithCleanupFixture(root =>
        {
            var stages = new List<string>();
            string Stage() { string path = CompletedStage(root, Guid.NewGuid().ToString("N")); stages.Add(path); return path; }
            File.Delete(Path.Combine(Stage(), PortableUpdateApply.ResultFileName));
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.ResultFileName), "not json");
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.ResultFileName), new string(' ', 4097));
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.ResultFileName),
                "{\"Success\":true,\"Message\":\"done\",\"CompletedUtc\":\"2026-10-18T00:00:00Z\",\"Success\":false}");
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.ResultFileName),
                "{\"Success\":true,\"Message\":\"done\",\"CompletedUtc\":\"invalid\"}");
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.ResultFileName),
                "{\"Success\":true,\"Message\":\"done\"}");
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.PlanFileName), "not json");
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.PlanFileName), new string(' ', 16385));
            File.WriteAllText(Path.Combine(Stage(), PortableUpdateApply.PlanFileName),
                JsonSerializer.Serialize(Plan() with { Id = Guid.NewGuid().ToString("N") }));
            Assert.AreEqual(0, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
            foreach (string stage in stages)
            {
                Assert.IsTrue(File.Exists(Path.Combine(stage, PortableUpdateApply.HelperFileName)));
                Assert.IsTrue(File.Exists(Path.Combine(stage, PortableUpdateApply.PayloadFileName)));
            }
        });
    }

    [TestMethod]
    public void CleanupSkipsLiveParentsAndExactActiveHelperImages()
    {
        WithCleanupFixture(root =>
        {
            string parentId = Guid.NewGuid().ToString("N");
            string helperId = Guid.NewGuid().ToString("N");
            string parent = CompletedStage(root, parentId);
            string helper = CompletedStage(root, helperId);
            string finished = CompletedStage(root, Guid.NewGuid().ToString("N"));
            Assert.AreEqual(1, PortableUpdateApply.CleanupCompletedStages(root,
                plan => plan.Id == parentId,
                image => image == Path.Combine(helper, PortableUpdateApply.HelperFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(parent, PortableUpdateApply.PayloadFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(helper, PortableUpdateApply.PayloadFileName)));
            Assert.IsFalse(File.Exists(Path.Combine(finished, PortableUpdateApply.PayloadFileName)));
        });
    }

    [TestMethod]
    public void CleanupLeavesUnknownFilesSubdirectoriesAndNonGuidFoldersUntouched()
    {
        WithCleanupFixture(root =>
        {
            string fileStage = CompletedStage(root, Guid.NewGuid().ToString("N"));
            string nestedStage = CompletedStage(root, Guid.NewGuid().ToString("N"));
            string nonGuidStage = CompletedStage(root, Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path.Combine(fileStage, "unrelated.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(nestedStage, "nested"));
            string unusual = Path.Combine(root, "unrecognized-folder");
            string boundary = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            Assert.IsTrue(Path.GetFullPath(nonGuidStage).StartsWith(boundary, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFullPath(unusual).StartsWith(boundary, StringComparison.OrdinalIgnoreCase));
            Directory.Move(nonGuidStage, unusual);
            Assert.AreEqual(0, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fileStage, "unrelated.txt")));
            foreach (string stage in new[] { fileStage, nestedStage, unusual })
                Assert.IsTrue(File.Exists(Path.Combine(stage, PortableUpdateApply.PayloadFileName)));
        });
    }

    [TestMethod]
    public void CleanupRejectsStagesThatAllowOtherUsersToModifyThem()
    {
        WithCleanupFixture(root =>
        {
            string stage = CompletedStage(root, Guid.NewGuid().ToString("N"));
            var directory = new DirectoryInfo(stage);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.WriteData, AccessControlType.Allow));
            directory.SetAccessControl(security);
            Assert.AreEqual(0, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
            Assert.IsTrue(File.Exists(Path.Combine(stage, PortableUpdateApply.PayloadFileName)));
        });
    }

    [TestMethod]
    public void CleanupProcessesAtMostThirtyTwoCompletedStagesPerInvocation()
    {
        WithCleanupFixture(root =>
        {
            for (int index = 0; index < 33; index++) CompletedStage(root, Guid.NewGuid().ToString("N"));
            Assert.AreEqual(32, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
            Assert.AreEqual(1, Directory.GetDirectories(root).Count(path => File.Exists(Path.Combine(path, PortableUpdateApply.PayloadFileName))));
            Assert.AreEqual(1, PortableUpdateApply.CleanupCompletedStages(root, _ => false, _ => false));
            Assert.AreEqual(33, Directory.GetDirectories(root).Length, "The result-only diagnostic directories are retained.");
        });
    }

    private static string CompletedStage(string root, string id, bool success = true)
    {
        string directory = Path.Combine(root, id);
        CreatePrivateCleanupDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.PlanFileName), JsonSerializer.Serialize(Plan() with { Id = id }));
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.HelperFileName), "old-helper");
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.PayloadFileName), "new-payload");
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.ReadyFileName), id);
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.CommitFileName), id);
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.AbortFileName), id);
        File.WriteAllText(Path.Combine(directory, PortableUpdateApply.ResultFileName), JsonSerializer.Serialize(new
        { Success = success, Message = success ? "Updated and restarted." : "The update was cancelled.", CompletedUtc = DateTimeOffset.UtcNow }));
        return directory;
    }

    private static void WithCleanupFixture(Action<string> test)
    {
        // TEMP and even AppData may grant unrelated principals write access.
        // Keep the production policy strict and use a private profile fixture.
        string parent = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "MouseWithoutBorders.UpdateCleanupTests"));
        Directory.CreateDirectory(parent);
        string directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        CreatePrivateCleanupDirectory(directory);
        try { test(directory); }
        finally
        {
            string absolute = Path.GetFullPath(directory);
            if (!absolute.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected cleanup test directory.");
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static void CreatePrivateCleanupDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(identity.User!);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    private static void Reject(string json) => Assert.ThrowsException<InvalidDataException>(
        () => PortableUpdateApply.ParsePlan(Encoding.UTF8.GetBytes(json)));

    private static void WithFixture(Action<string, string, string, string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MouseWithoutBorders.UpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "MouseWithoutBorders.exe");
            string payload = Path.Combine(directory, "download.exe");
            string prefs = Path.Combine(directory, PortableApplication.SettingsFileName);
            File.WriteAllText(target, "old-exe");
            File.WriteAllText(payload, "new-exe");
            File.WriteAllText(prefs, "{\"security-key\":\"test-secret-must-stay-byte-for-byte\"}\n");
            test(directory, target, payload, prefs);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
