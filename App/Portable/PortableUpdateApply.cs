// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
#if PORTABLE_SINGLE_FILE
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using MouseWithoutBorders.SecureDesktop;

namespace MouseWithoutBorders.Core;

/// <summary>
/// EXE-only update of the normal, current-user copy. The temporary helper is an
/// exact copy of that running EXE and inherits its token; it has no service,
/// registry, firewall, preferences, arbitrary command, or elevation operation.
/// </summary>
internal static class PortableUpdateApply
{
    internal const string ApplyArgument = "--mwb-apply-update";
    internal const string PlanFileName = "apply.json";
    internal const string HelperFileName = "MouseWithoutBorders.UpdateHelper.exe";
    internal const string PayloadFileName = "update.exe";
    internal const string ReadyFileName = "ready.txt";
    internal const string CommitFileName = "commit.txt";
    internal const string AbortFileName = "abort.txt";
    internal const string ResultFileName = "result.json";
    private const int MaximumPlanBytes = 16 * 1024;
    private const int MaximumResultBytes = 4096;
    private const int MaximumCleanupInspections = 4096;
    private const int MaximumCleanupStages = 32;
    private static readonly StringComparison Paths = StringComparison.OrdinalIgnoreCase;

    internal sealed record UpdatePlan(int Version, string Id, int ParentPid, long ParentStartedTicks,
        int SessionId, string TargetPath, string OldSha256, string NewSha256);

    internal sealed class PreparedUpdate : IDisposable
    {
        private readonly UpdatePlan plan;
        private bool started;
        private bool cancelled;
        internal string PlanPath { get; }
        internal string HelperPath => Path.Combine(Path.GetDirectoryName(PlanPath)!, HelperFileName);
        internal string ResultPath => Path.Combine(Path.GetDirectoryName(PlanPath)!, ResultFileName);

        internal PreparedUpdate(UpdatePlan plan, string planPath) { this.plan = plan; PlanPath = planPath; }

        /// <summary>The caller must quit only after this completes successfully.</summary>
        internal async Task StartAndWaitReadyAsync(TimeSpan timeout, CancellationToken cancellation = default)
        {
            try { await StartAndWaitReadyCoreAsync(timeout, cancellation).ConfigureAwait(false); }
            catch { CancelPending(); throw; }
        }

        private async Task StartAndWaitReadyCoreAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            if (started) throw new InvalidOperationException("This update has already started.");
            if (cancelled) throw new OperationCanceledException("This update was cancelled.");
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            RequireNormalUser();
            ValidatePreparedFiles(plan, PlanPath, HelperPath);
            var info = new ProcessStartInfo(HelperPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(HelperPath),
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            info.ArgumentList.Add(ApplyArgument);
            info.ArgumentList.Add(PlanPath);
            using var helper = Process.Start(info) ?? throw new IOException("Windows could not start the update helper.");
            started = true;
            var clock = Stopwatch.StartNew();
            string ready = Path.Combine(Path.GetDirectoryName(PlanPath)!, ReadyFileName);
            while (clock.Elapsed < timeout)
            {
                cancellation.ThrowIfCancellationRequested();
                if (helper.HasExited)
                    throw new IOException("The update helper could not validate this application. The running copy has not been closed.");
                if (File.Exists(ready))
                {
                    RequireSafeEntry(new FileInfo(ready), includeCreateRights: true);
                    if (new FileInfo(ready).Length <= 64 && File.ReadAllText(ready) == plan.Id)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        WriteNewFile(Path.Combine(Path.GetDirectoryName(PlanPath)!, CommitFileName), Encoding.UTF8.GetBytes(plan.Id));
                        return;
                    }
                    throw new SecurityException("The update helper returned an invalid ready record.");
                }
                await Task.Delay(50, cancellation).ConfigureAwait(false);
            }
            // Do not kill any process. The helper independently times out while
            // the verified parent continues running, without replacing files.
            throw new TimeoutException("The update helper did not become ready. The running copy has not been closed.");
        }

        internal void CancelPending()
        {
            cancelled = true;
            if (!started) return;
            try
            {
                ValidatePlanLocation(PlanPath);
                string abort = Path.Combine(Path.GetDirectoryName(PlanPath)!, AbortFileName);
                if (!File.Exists(abort)) WriteNewFile(abort, Encoding.UTF8.GetBytes(plan.Id));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }

        public void Dispose()
        {
            if (!started) DeletePreparedFiles(Path.GetDirectoryName(PlanPath)!);
        }
    }

    internal static bool IsApplyInvocation(string[] args) => args.Length > 1
        && string.Equals(args[1], ApplyArgument, StringComparison.Ordinal);

    internal static PreparedUpdate Prepare(string downloadedExecutablePath, string expectedSha256)
    {
        RequireNormalUser();
        if (!IsHash(expectedSha256)) throw new InvalidDataException("The update checksum is invalid.");
        using var parent = Process.GetCurrentProcess();
        string target = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("The current EXE path is unavailable."));
        ValidateNormalTarget(target);
        string updates = UpdatesDirectory;
        Directory.CreateDirectory(updates);
        ValidateAncestors(updates);
        RequireSafeEntry(new DirectoryInfo(updates), includeCreateRights: true);
        CleanupCompletedStages();
        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(updates, id);
        CreatePrivateDirectory(staging);
        try
        {
            string helper = Path.Combine(staging, HelperFileName);
            string payload = Path.Combine(staging, PayloadFileName);
            CopyNewFile(target, helper);
            CopyNewFile(Path.GetFullPath(downloadedExecutablePath), payload);
            string oldHash = HashFile(helper);
            RequireHash(target, oldHash);
            RequireHash(payload, expectedSha256);
            var plan = new UpdatePlan(1, id, parent.Id, parent.StartTime.ToUniversalTime().Ticks,
                parent.SessionId, target, oldHash, expectedSha256.ToUpperInvariant());
            string planPath = Path.Combine(staging, PlanFileName);
            WriteNewFile(planPath, JsonSerializer.SerializeToUtf8Bytes(plan));
            ValidatePreparedFiles(plan, planPath, helper);
            return new PreparedUpdate(plan, planPath);
        }
        catch { DeletePreparedFiles(staging); throw; }
    }

    /// <summary>Called before any normal application/helper/service initialization.</summary>
    internal static int RunApply(string[] args)
    {
        UpdatePlan? plan = null;
        string? staging = null;
        bool parentValidated = false;
        bool transactionStarted = false;
        try
        {
            RequireNormalUser();
            if (args.Length != 3 || !IsApplyInvocation(args)) throw new SecurityException("Invalid update helper arguments.");
            string planPath = CanonicalPath(args[2]);
            staging = Path.GetDirectoryName(planPath)!;
            ValidatePlanLocation(planPath);
            RequireSafeEntry(new FileInfo(planPath), includeCreateRights: true);
            using (var stream = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumPlanBytes) throw new InvalidDataException("Invalid update plan size.");
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                plan = ParsePlan(buffer.ToArray());
            }
            string self = CanonicalPath(Environment.ProcessPath ?? string.Empty);
            ValidatePreparedFiles(plan, planPath, self);
            using var parent = Process.GetProcessById(plan.ParentPid);
            // A fabricated PID/start time is insufficient: the helper must have
            // actually been created by the verified, still-running target EXE.
            if (GetParentProcessId() != plan.ParentPid || !MatchesParent(plan, parent.Id,
                    parent.StartTime.ToUniversalTime().Ticks, parent.SessionId, parent.MainModule?.FileName ?? string.Empty)
                || parent.HasExited || parent.SessionId != Process.GetCurrentProcess().SessionId)
                throw new SecurityException("The update request is not from the running normal application.");
            RequireSameProcessUser(parent);
            parentValidated = true;
            var peers = CaptureSameCopyProcesses(plan);
            try
            {
                WriteNewFile(Path.Combine(staging, ReadyFileName), Encoding.UTF8.GetBytes(plan.Id));
                WaitForCommit(staging, plan.Id, parent, TimeSpan.FromSeconds(30));
                WaitForProcesses(parent, peers, staging, plan.Id, TimeSpan.FromSeconds(30));
                transactionStarted = true;
                ApplyTransaction(plan.TargetPath, Path.Combine(staging, PayloadFileName), plan.OldSha256,
                    plan.NewSha256, plan.Id,
                    () => { ValidateNormalTarget(plan.TargetPath); ValidatePreparedFiles(plan, planPath, self); },
                    LaunchAndCheck, () => CheckNotAborted(staging, plan.Id));
                WriteResult(staging, true, "Updated and restarted.");
                return 0;
            }
            finally { foreach (var peer in peers) peer.Dispose(); }
        }
        catch (Exception error)
        {
            if (parentValidated && !transactionStarted && plan != null && ParentHasExited(plan))
            {
                // The parent may have quit just before a payload recheck or a
                // clipboard-helper timeout. Relaunch only its unchanged old EXE.
                try { ValidateNormalTarget(plan.TargetPath); RequireHash(plan.TargetPath, plan.OldSha256); LaunchAndCheck(plan.TargetPath); }
                catch (Exception) { }
            }
            // Write only to an already validated private staging directory.
            // There is no fallback operation on a caller-supplied destination.
            if (staging != null && plan != null)
            {
                try { ValidatePlanLocation(Path.Combine(staging, PlanFileName)); WriteResult(staging, false, error.Message); }
                catch (Exception) { }
            }
            Debug.WriteLine("Portable update helper failed: " + error.GetType().Name);
            return 1;
        }
    }

    internal static UpdatePlan ParsePlan(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumPlanBytes) throw new InvalidDataException("Invalid update plan size.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid update plan.");
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "Version", "Id", "ParentPid", "ParentStartedTicks", "SessionId", "TargetPath", "OldSha256", "NewSha256" };
            foreach (var property in root.EnumerateObject())
                if (!allowed.Remove(property.Name)) throw new InvalidDataException("Unknown or duplicate update plan field.");
            if (allowed.Count != 0) throw new InvalidDataException("Incomplete update plan.");
            var plan = new UpdatePlan(root.GetProperty("Version").GetInt32(), root.GetProperty("Id").GetString()!,
                root.GetProperty("ParentPid").GetInt32(), root.GetProperty("ParentStartedTicks").GetInt64(),
                root.GetProperty("SessionId").GetInt32(), root.GetProperty("TargetPath").GetString()!,
                root.GetProperty("OldSha256").GetString()!, root.GetProperty("NewSha256").GetString()!);
            if (plan.Version != 1 || !IsId(plan.Id) || plan.ParentPid <= 0 || plan.ParentStartedTicks <= 0
                || plan.SessionId <= 0 || !IsHash(plan.OldSha256) || !IsHash(plan.NewSha256)
                || string.IsNullOrWhiteSpace(plan.TargetPath)
                || !Path.IsPathFullyQualified(plan.TargetPath) || plan.TargetPath != CanonicalPath(plan.TargetPath)
                || !Path.GetFileName(plan.TargetPath).Equals("MouseWithoutBorders.exe", Paths))
                throw new InvalidDataException("Invalid update plan values.");
            return plan;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
        { throw new InvalidDataException("Invalid update plan.", error); }
    }

    internal static bool MatchesParent(UpdatePlan plan, int id, long started, int sessionId, string executable) =>
        id == plan.ParentPid && started == plan.ParentStartedTicks && sessionId == plan.SessionId
        && string.Equals(plan.TargetPath, Path.GetFullPath(executable), Paths);

    /// <summary>
    /// Transaction seam for filesystem and failed-start tests. Production callers
    /// always supply the validated parent-bound paths above, never CLI targets.
    /// </summary>
    internal static void ApplyTransaction(string target, string payload, string oldHash, string newHash, string id,
        Action validate, Func<string, bool> launchAndCheck, Action? abortCheck = null)
    {
        if (!IsId(id) || !IsHash(oldHash) || !IsHash(newHash)) throw new InvalidDataException("Invalid update transaction.");
        string sibling = Path.Combine(Path.GetDirectoryName(target)!, ".mwb-update-" + id + ".tmp");
        string backup = target + ".mwb-update-" + id + ".bak";
        bool replaced = false;
        bool committed = false;
        bool siblingCreated = false;
        try
        {
            abortCheck?.Invoke();
            validate();
            RequireHash(target, oldHash);
            RequireHash(payload, newHash);
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("An update recovery file already exists.");
            CopyNewFile(payload, sibling, () => siblingCreated = true);
            validate();
            RequireHash(target, oldHash);
            RequireHash(payload, newHash);
            RequireHash(sibling, newHash);
            abortCheck?.Invoke();
            // Atomic replacement preserves the original file's ACL and leaves a
            // complete old EXE for recovery. No preferences are copied or edited.
            File.Replace(sibling, target, backup, ignoreMetadataErrors: false);
            siblingCreated = false;
            replaced = true;
            RequireHash(target, newHash);
            if (!launchAndCheck(target)) throw new IOException("The updated application exited during startup.");
            committed = true;
            try { File.Delete(backup); }
            catch (IOException) { /* The running new copy is healthy; retain the old backup. */ }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception updateError)
        {
            if (!replaced)
            {
                // The real helper invokes this only after the verified parent
                // exited. Recover its unchanged old image, never an unverified
                // destination, even when cancellation wins before replacement.
                try { RequireHash(target, oldHash); launchAndCheck(target); }
                catch (Exception) { }
            }
            if (replaced && !committed)
            {
                try
                {
                    RequireHash(backup, oldHash);
                    // Keep the recovery EXE until the old application also
                    // survives startup; a failed recovery must not consume it.
                    CopyNewFile(backup, sibling, () => siblingCreated = true);
                    RequireHash(sibling, oldHash);
                    File.Replace(sibling, target, null, ignoreMetadataErrors: false);
                    siblingCreated = false;
                    RequireHash(target, oldHash);
                    if (!launchAndCheck(target)) throw new IOException("The previous application could not restart.");
                    File.Delete(backup);
                }
                catch (Exception recoveryError)
                {
                    throw new IOException("The update failed and automatic recovery could not finish. Keep the recovery EXE at "
                        + backup + ". " + recoveryError.Message, updateError);
                }
            }
            throw;
        }
        finally
        {
            // This exact random sibling was created by this transaction. Never
            // recurse or delete recovery files after a failed rollback.
            if (siblingCreated && File.Exists(sibling)) File.Delete(sibling);
        }
    }

    internal static void RequireHash(string path, string expected)
    {
        if (!IsHash(expected) || !string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update executable checksum does not match.");
    }

    private static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string UpdatesDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".MouseWithoutBorders", "Updates"));

    private static void CleanupCompletedStages()
    {
        try
        {
            var activeHelpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var helper in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperFileName)))
            {
                using (helper)
                {
                    try
                    {
                        if (!helper.HasExited)
                        {
                            string? image = helper.MainModule?.FileName;
                            // An uninspectable helper could belong to a live
                            // stage. Cleanup is optional: fail closed globally.
                            if (string.IsNullOrEmpty(image)) return;
                            activeHelpers.Add(Path.GetFullPath(image));
                        }
                    }
                    catch (InvalidOperationException) { return; }
                    catch (Win32Exception) { return; }
                }
            }
            CleanupCompletedStages(UpdatesDirectory, IsRecordedParentAlive, activeHelpers.Contains);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException
            or Win32Exception or InvalidOperationException)
        { Debug.WriteLine("Completed update cleanup skipped: " + error.GetType().Name); }
    }

    /// <summary>
    /// Reclaims only fixed files from completed, inactive stages. Result records
    /// remain for diagnostics; active, malformed and unfamiliar stages stay put.
    /// The injected liveness checks exercise this policy without live processes.
    /// </summary>
    internal static int CleanupCompletedStages(string updatesRoot, Func<UpdatePlan, bool> isParentAlive,
        Func<string, bool> isHelperActive)
    {
        ArgumentNullException.ThrowIfNull(isParentAlive);
        ArgumentNullException.ThrowIfNull(isHelperActive);
        string root = CanonicalPath(updatesRoot);
        if (!Directory.Exists(root)) return 0;
        ValidateAncestors(root);
        RequireSafeEntry(new DirectoryInfo(root), includeCreateRights: true);
        int inspected = 0;
        int cleaned = 0;
        var names = new HashSet<string>(StringComparer.Ordinal)
        { PlanFileName, HelperFileName, PayloadFileName, ReadyFileName, CommitFileName, AbortFileName, ResultFileName };
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (++inspected > MaximumCleanupInspections || cleaned >= MaximumCleanupStages) break;
            if (!IsId(Path.GetFileName(directory))) continue;
            try
            {
                RequireSafeEntry(new DirectoryInfo(directory), includeCreateRights: true);
                string planPath = Path.Combine(directory, PlanFileName);
                string resultPath = Path.Combine(directory, ResultFileName);
                if (!File.Exists(planPath) || !File.Exists(resultPath)) continue;
                int entries = 0;
                bool unfamiliar = false;
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++entries > names.Count || !names.Contains(Path.GetFileName(entry)) || Directory.Exists(entry))
                    { unfamiliar = true; break; }
                    RequireSafeEntry(new FileInfo(entry), includeCreateRights: true);
                }
                if (unfamiliar) continue;
                var plan = ParsePlan(ReadBoundedFile(planPath, MaximumPlanBytes));
                if (plan.Id != Path.GetFileName(directory)
                    || !IsCompletedResult(ReadBoundedFile(resultPath, MaximumResultBytes))
                    || isParentAlive(plan)
                    || isHelperActive(Path.Combine(directory, HelperFileName))) continue;

                // Delete the plan last. If a locked file prevents cleanup, the
                // remaining completed stage can be reconsidered on a later try.
                foreach (string name in new[] { HelperFileName, PayloadFileName, ReadyFileName, CommitFileName, AbortFileName, PlanFileName })
                {
                    string path = Path.Combine(directory, name);
                    if (!File.Exists(path)) continue;
                    RequireSafeEntry(new FileInfo(path), includeCreateRights: true);
                    File.Delete(path);
                }
                cleaned++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException
                or InvalidOperationException or Win32Exception)
            { Debug.WriteLine("Completed update stage retained: " + error.GetType().Name); }
        }
        return cleaned;
    }

    private static byte[] ReadBoundedFile(string path, int limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > limit) throw new InvalidDataException("Invalid update record size.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static bool IsCompletedResult(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "Success", "Message", "CompletedUtc" };
            foreach (var property in root.EnumerateObject())
                if (!allowed.Remove(property.Name)) return false;
            return allowed.Count == 0
                && (root.GetProperty("Success").ValueKind is JsonValueKind.True or JsonValueKind.False)
                && root.GetProperty("Message").ValueKind == JsonValueKind.String
                && root.GetProperty("CompletedUtc").ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(root.GetProperty("CompletedUtc").GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsRecordedParentAlive(UpdatePlan plan)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentPid);
            return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartedTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private static void RequireNormalUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (SecureDesktopAgent.IsActive || identity.User == null || identity.IsSystem
            || identity.User.IsWellKnown(WellKnownSidType.LocalServiceSid)
            || identity.User.IsWellKnown(WellKnownSidType.NetworkServiceSid))
            throw new SecurityException("Automatic updates are available only in the normal user application.");
    }

    internal static void ValidateNormalTarget(string target)
    {
        target = CanonicalPath(target);
        string profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!Path.GetFileName(target).Equals("MouseWithoutBorders.exe", Paths) || !IsBelow(target, profile)
            || target.StartsWith(@"\\", StringComparison.Ordinal)
            || IsBelow(target, ServiceTrust.InstallDirectory) || IsBelow(target, ServiceTrust.ProfileDirectory)
            || IsBelow(target, UpdatesDirectory))
            throw new SecurityException("This location needs a manual update. Use a normal copy in your own user folder.");
        foreach (var location in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
        {
            string protectedRoot = Environment.GetFolderPath(location);
            if (!string.IsNullOrEmpty(protectedRoot) && IsBelow(target, protectedRoot))
                throw new SecurityException("Protected installations require a manual update.");
        }
        if (new DriveInfo(Path.GetPathRoot(target)!).DriveType != DriveType.Fixed)
            throw new SecurityException("Automatic updates require a local fixed drive.");
        ValidateAncestors(Path.GetDirectoryName(target)!);
        RequireSafeEntry(new DirectoryInfo(Path.GetDirectoryName(target)!), includeCreateRights: true);
        RequireSafeEntry(new FileInfo(target), includeCreateRights: true);
    }

    private static void ValidatePlanLocation(string planPath)
    {
        string directory = Path.GetDirectoryName(planPath)!;
        if (!Path.GetFileName(planPath).Equals(PlanFileName, StringComparison.Ordinal)
            || !IsId(Path.GetFileName(directory))
            || !string.Equals(Path.GetDirectoryName(directory), UpdatesDirectory, Paths))
            throw new SecurityException("The update plan is outside its fixed staging directory.");
        ValidateAncestors(directory);
        RequireSafeEntry(new DirectoryInfo(directory), includeCreateRights: true);
    }

    private static void ValidatePreparedFiles(UpdatePlan plan, string planPath, string helperPath)
    {
        ValidatePlanLocation(planPath);
        string staging = Path.GetDirectoryName(planPath)!;
        if (Path.GetFileName(staging) != plan.Id || !string.Equals(CanonicalPath(helperPath),
                Path.Combine(staging, HelperFileName), Paths))
            throw new SecurityException("The update helper is outside the prepared directory.");
        ValidateNormalTarget(plan.TargetPath);
        foreach (string path in new[] { planPath, helperPath, Path.Combine(staging, PayloadFileName) })
            RequireSafeEntry(new FileInfo(path), includeCreateRights: true);
        RequireHash(helperPath, plan.OldSha256);
        RequireHash(plan.TargetPath, plan.OldSha256);
        RequireHash(Path.Combine(staging, PayloadFileName), plan.NewSha256);
    }

    private static void ValidateAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            RequireSafeEntry(directory, includeCreateRights: false);
    }

    private static void RequireSafeEntry(FileSystemInfo entry, bool includeCreateRights)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("An update path is missing or is a reparse point.");
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User!.Value;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || (owner.Value != user && !ServiceTrust.IsTrustedIdentity(owner.Value)))
            throw new SecurityException("An update path is owned by another user.");
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if ((raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || raw.DiscretionaryAcl == null)
            throw new SecurityException("An update path does not have a restrictive ACL.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (((SecurityIdentifier)rule.IdentityReference).Value != user
                && ServiceTrust.IsUnsafeAccessRule(rule, includeCreateRights, requirePrivateRead: false))
                throw new SecurityException("An update path permits another user to modify it.");
        }
    }

    private static void CreatePrivateDirectory(string path)
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

    private static List<Process> CaptureSameCopyProcesses(UpdatePlan plan)
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName("MouseWithoutBorders"))
        {
            bool keep = false;
            try
            {
                if (process.Id != plan.ParentPid && process.Id != Environment.ProcessId && !process.HasExited
                    && process.SessionId == plan.SessionId
                    && string.Equals(process.MainModule?.FileName, plan.TargetPath, Paths))
                { matches.Add(process); keep = true; }
            }
            catch (InvalidOperationException) { }
            // The optional SYSTEM agent may share the image name and session,
            // but its protected image cannot always be inspected by this user.
            // Never stop, wait on, or otherwise manipulate an unverified image.
            catch (Win32Exception) { }
            finally { if (!keep) process.Dispose(); }
        }
        return matches;
    }

    private static void WaitForProcesses(Process parent, List<Process> peers, string staging, string id, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        WaitForExitOrAbort(() => parent.HasExited && peers.All(p => p.HasExited),
            () => File.Exists(Path.Combine(staging, AbortFileName)), () => clock.ElapsedMilliseconds, Thread.Sleep, timeout);
        CheckNotAborted(staging, id);
    }

    internal static void WaitForExitOrAbort(Func<bool> hasExited, Func<bool> isAborted, Func<long> elapsedMilliseconds,
        Action<int> pause, TimeSpan timeout)
    {
        while (true)
        {
            if (isAborted()) throw new OperationCanceledException("The application cancelled this update. No executable was replaced.");
            if (hasExited())
            {
                if (isAborted()) throw new OperationCanceledException("The application cancelled this update. No executable was replaced.");
                return;
            }
            if (elapsedMilliseconds() >= timeout.TotalMilliseconds)
                throw new TimeoutException("Mouse Without Borders is still running. No executable was replaced.");
            pause(50);
        }
    }

    private static void WaitForCommit(string staging, string id, Process parent, TimeSpan timeout)
    {
        string commit = Path.Combine(staging, CommitFileName);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            CheckNotAborted(staging, id);
            if (File.Exists(commit))
            {
                RequireSafeEntry(new FileInfo(commit), true);
                if (new FileInfo(commit).Length <= 64 && File.ReadAllText(commit) == id) return;
                throw new SecurityException("The update commit record is invalid.");
            }
            if (parent.HasExited) throw new IOException("The application closed before accepting the validated update helper.");
            Thread.Sleep(50);
        }
        throw new TimeoutException("The application did not accept the update helper. No executable was replaced.");
    }

    private static void CheckNotAborted(string staging, string id)
    {
        // Any abort marker is fail-closed, even a truncated or malformed one.
        if (File.Exists(Path.Combine(staging, AbortFileName)))
            throw new OperationCanceledException("The application cancelled this update. No executable was replaced.");
    }

    private static bool ParentHasExited(UpdatePlan plan)
    {
        try
        {
            using var parent = Process.GetProcessById(plan.ParentPid);
            return parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedTicks;
        }
        catch (ArgumentException) { return true; }
    }

    private static bool LaunchAndCheck(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) })
            ?? throw new IOException("Windows could not restart Mouse Without Borders.");
        // This is an initial startup check, not a claim of two-PC connectivity.
        return !process.WaitForExit(3000);
    }

    private static void CopyNewFile(string source, string destination, Action? created = null)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        created?.Invoke();
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    private static void WriteResult(string staging, bool success, string message) => WriteNewFile(
        Path.Combine(staging, ResultFileName), JsonSerializer.SerializeToUtf8Bytes(new
        { Success = success, Message = message, CompletedUtc = DateTimeOffset.UtcNow }));

    private static void DeletePreparedFiles(string directory)
    {
        try
        {
            ValidatePlanLocation(Path.Combine(directory, PlanFileName));
            foreach (string name in new[] { PlanFileName, HelperFileName, PayloadFileName })
            {
                string file = Path.Combine(directory, name);
                if (File.Exists(file)) { RequireSafeEntry(new FileInfo(file), true); File.Delete(file); }
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception) { /* Leave bounded, non-secret staging on failed cleanup. */ }
    }

    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsId(string? value) => value?.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsBelow(string path, string directory) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, Paths);

    private static string CanonicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0
            || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Skip(1).Any(p => p.EndsWith(' ') || p.EndsWith('.')))
            throw new SecurityException("The update path is not an ordinary absolute local path.");
        return Path.GetFullPath(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr ExitStatus;
        internal IntPtr PebBaseAddress;
        internal IntPtr AffinityMask;
        internal IntPtr BasePriority;
        internal UIntPtr UniqueProcessId;
        internal UIntPtr InheritedFromUniqueProcessId;
    }

    private static int GetParentProcessId()
    {
        using var current = Process.GetCurrentProcess();
        int status = NtQueryInformationProcess(current.Handle, 0, out var info,
            Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0 || info.InheritedFromUniqueProcessId.ToUInt64() > int.MaxValue)
            throw new SecurityException("The update helper parent could not be verified.");
        return (int)info.InheritedFromUniqueProcessId.ToUInt64();
    }

    private static void RequireSameProcessUser(Process process)
    {
        if (!OpenProcessToken(process.Handle, 0x0008, out var token))
            throw new SecurityException("The update request process owner could not be verified.");
        using (token)
        using (var parentIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var currentIdentity = WindowsIdentity.GetCurrent())
            if (parentIdentity.User == null || currentIdentity.User == null || !parentIdentity.User.Equals(currentIdentity.User))
                throw new SecurityException("The update request belongs to another user.");
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass,
        out ProcessBasicInformation information, int informationLength, out int returnLength);
}
#endif
