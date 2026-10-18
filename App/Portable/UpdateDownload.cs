// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MouseWithoutBorders.SecureDesktop;

namespace MouseWithoutBorders.Core;

internal sealed record UpdateDownloadProgress(long Received, long Total);

/// <summary>A verified download, never an instruction to execute it.</summary>
internal sealed class DownloadedUpdate : IDisposable
{
    private int disposed;
    internal string DirectoryPath { get; }
    internal string ExecutablePath { get; }
    internal string Sha256 { get; }
    internal string PartialPath => ExecutablePath + ".part";

    internal DownloadedUpdate(string directory, string sha256)
    {
        DirectoryPath = directory;
        ExecutablePath = Path.Combine(directory, "MouseWithoutBorders.exe");
        Sha256 = sha256;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            UpdateDownload.RequireSafeDownloadDirectory(DirectoryPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine("Unsafe update cleanup path retained: " + error.GetType().Name);
            return;
        }
        // Never recurse: a caller or a concurrent process may have added an
        // unrelated file. An incomplete cleanup may be inspected later.
        foreach (string path in new[] { PartialPath, ExecutablePath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                UpdateDownload.RequireSafeDownloadFile(path);
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            { Debug.WriteLine("Update download cleanup: " + error.GetType().Name); }
        }
        try { Directory.Delete(DirectoryPath, recursive: false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Debug.WriteLine("Update download cleanup: " + error.GetType().Name); }
    }
}

internal static class UpdateDownload
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    internal static async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release,
        IProgress<UpdateDownloadProgress> progress, CancellationToken token)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            Credentials = null,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".MouseWithoutBorders", "downloads");
        return await DownloadAsync(release, progress, token, http, root).ConfigureAwait(false);
    }

    // The injected client must not follow redirects automatically. Production
    // creates the handler above; tests provide an in-memory message handler.
    internal static async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release,
        IProgress<UpdateDownloadProgress> progress, CancellationToken token, HttpClient http, string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(http);
        ValidateAsset(release, release.Executable, "MouseWithoutBorders.exe", ManualUpdates.MaximumExecutableSize);
        ValidateAsset(release, release.Checksum, "MouseWithoutBorders.exe.sha256", ManualUpdates.MaximumChecksumSize);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(DownloadTimeout);
        token = timeout.Token;
        token.ThrowIfCancellationRequested();

        byte[] checksumBytes = await ReadChecksumAsync(release.Checksum, http, token).ConfigureAwait(false);
        string checksumDigest = Convert.ToHexString(SHA256.HashData(checksumBytes));
        if (release.Checksum.Sha256Digest != null && checksumDigest != release.Checksum.Sha256Digest)
            throw new InvalidDataException("The checksum download does not match its GitHub digest.");
        string expectedHash = ParseChecksum(new UTF8Encoding(false, true).GetString(checksumBytes));
        if (release.Executable.Sha256Digest != null && expectedHash != release.Executable.Sha256Digest)
            throw new InvalidDataException("The application checksum does not match its GitHub digest.");

        var downloaded = new DownloadedUpdate(CreatePrivateStage(stagingRoot), expectedHash);
        try
        {
            using var response = await GetResponseAsync(http, release.Executable.DownloadUri, token).ConfigureAwait(false);
            ValidateContentLength(response, release.Executable.Size);
            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using (var output = FileSystemAclExtensions.Create(new FileInfo(downloaded.PartialPath),
                FileMode.CreateNew, FileSystemRights.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan, PrivateFileSecurity()))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[BufferSize];
                long received = 0;
                progress?.Report(new UpdateDownloadProgress(0, release.Executable.Size));
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (count > release.Executable.Size - received)
                        throw new InvalidDataException("The application download exceeds the declared release size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                    progress?.Report(new UpdateDownloadProgress(received, release.Executable.Size));
                }
                if (received != release.Executable.Size)
                    throw new InvalidDataException("The application download is incomplete.");
                string actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The application SHA-256 checksum is incorrect.");
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            ValidateExecutable(downloaded.PartialPath, release.Version);
            token.ThrowIfCancellationRequested();
            File.Move(downloaded.PartialPath, downloaded.ExecutablePath);
            return downloaded;
        }
        catch
        {
            downloaded.Dispose();
            throw;
        }
    }

    private static void ValidateAsset(UpdateRelease release, UpdateAsset asset, string name, long limit)
    {
        if (string.IsNullOrEmpty(release.Version) || release.Version.Length > 128
            || !Regex.IsMatch(release.Version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?\z")
            || !string.Equals(release.Tag, "mwb-v" + release.Version, StringComparison.Ordinal)
            || asset == null || asset.Name != name || asset.Size < 1 || asset.Size > limit
            || asset.DownloadUri == null || !IsAllowedDownloadUri(asset.DownloadUri)
            || !string.Equals(asset.DownloadUri.OriginalString,
                ManualUpdates.Releases + "/download/" + Uri.EscapeDataString(release.Tag) + "/" + name, StringComparison.Ordinal)
            || (asset.Sha256Digest != null && !Regex.IsMatch(asset.Sha256Digest, @"\A[0-9A-F]{64}\z")))
            throw new InvalidDataException("The release download metadata is invalid.");
    }

    private static async Task<byte[]> ReadChecksumAsync(UpdateAsset asset, HttpClient http, CancellationToken token)
    {
        using var response = await GetResponseAsync(http, asset.DownloadUri, token).ConfigureAwait(false);
        ValidateContentLength(response, asset.Size);
        using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream((int)asset.Size);
        byte[] buffer = new byte[ManualUpdates.MaximumChecksumSize];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (count > asset.Size - memory.Length)
                throw new InvalidDataException("The checksum download exceeds the declared release size.");
            await memory.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (memory.Length != asset.Size) throw new InvalidDataException("The checksum download is incomplete.");
        return memory.ToArray();
    }

    internal static string ParseChecksum(string text)
    {
        // Accept SHA256SUM's text/binary filename marker, UTF-8 BOM and one
        // trailing line ending. A second line, alternate name or bare hash is
        // ambiguous and is rejected before the larger executable download.
        var match = Regex.Match(text ?? "", @"\A\uFEFF?([0-9A-Fa-f]{64})[ \t]+\*?MouseWithoutBorders\.exe(?:\r\n|\n|\r)?\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("The release checksum file is invalid.");
        return match.Groups[1].Value.ToUpperInvariant();
    }

    private static void ValidateContentLength(HttpResponseMessage response, long expected)
    {
        if (response.Content.Headers.ContentLength is long declared && declared != expected)
            throw new InvalidDataException("The download Content-Length differs from the release metadata.");
    }

    internal static bool IsAllowedDownloadUri(Uri uri) => uri != null && uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static async Task<HttpResponseMessage> GetResponseAsync(HttpClient http, Uri uri, CancellationToken token)
    {
        for (int redirects = 0; ; redirects++)
        {
            if (!IsAllowedDownloadUri(uri)) throw new InvalidDataException("GitHub redirected the download to an unsupported address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MouseWithoutBorders-Portable-Updater");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= MaximumRedirects || response.Headers.Location == null)
                        throw new InvalidDataException("GitHub returned too many or invalid download redirects.");
                    uri = new Uri(uri, response.Headers.Location);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static string CreatePrivateStage(string stagingRoot)
    {
        string root = Path.GetFullPath(stagingRoot);
        RequireSafeAncestors(root, allowMissing: true);
        Directory.CreateDirectory(root);
        RequireSafeDownloadDirectory(root);
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("The private update directory already exists.");
        var security = new DirectorySecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in PrivateSids(identity.User))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        RequireSafeDownloadDirectory(directory);
        return directory;
    }

    private static FileSecurity PrivateFileSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetOwner(identity.User);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in PrivateSids(identity.User))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static SecurityIdentifier[] PrivateSids(SecurityIdentifier user) => new[]
    {
        user,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    };

    internal static void RequireSafeDownloadDirectory(string path)
    {
        RequireSafeAncestors(path, allowMissing: false);
        RequireSafeEntry(new DirectoryInfo(path), includeCreateRights: true);
    }

    internal static void RequireSafeDownloadFile(string path) => RequireSafeEntry(new FileInfo(path), includeCreateRights: true);

    private static void RequireSafeAncestors(string path, bool allowMissing)
    {
        for (var cursor = new DirectoryInfo(path); cursor != null; cursor = cursor.Parent)
        {
            if (!cursor.Exists && allowMissing) continue;
            RequireSafeEntry(cursor, includeCreateRights: false);
        }
    }

    private static void RequireSafeEntry(FileSystemInfo entry, bool includeCreateRights)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("The update download path is missing or contains a symbolic link or junction.");
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new SecurityException("The update user could not be identified.");
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || (owner.Value != user && !ServiceTrust.IsTrustedIdentity(owner.Value)))
            throw new SecurityException("The update download path belongs to another user.");
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl == null)
            throw new SecurityException("The update download path does not have a restrictive ACL.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (((SecurityIdentifier)rule.IdentityReference).Value != user
                && ServiceTrust.IsUnsafeAccessRule(rule, includeCreateRights, requirePrivateRead: false))
                throw new SecurityException("The update download path permits another principal to modify it.");
    }

    internal static void ValidateExecutable(string path, string expectedVersion)
    {
        ValidatePortableExecutable(path, RuntimeInformation.ProcessArchitecture);
        string productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        if (string.IsNullOrEmpty(productVersion) || string.IsNullOrEmpty(expectedVersion)
            || !string.Equals(WithoutBuildMetadata(productVersion), WithoutBuildMetadata(expectedVersion), StringComparison.Ordinal))
            throw new InvalidDataException("The downloaded application version differs from the release tag.");
    }

    private static string WithoutBuildMetadata(string version)
    {
        int separator = version.IndexOf('+');
        return separator < 0 ? version : version.Substring(0, separator);
    }

    internal static void ValidatePortableExecutable(string path, Architecture architecture)
    {
        ushort expectedMachine = architecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            _ => throw new PlatformNotSupportedException("Application updates support x64 and ARM64 only."),
        };
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> dos = stackalloc byte[64];
        if (input.Length < dos.Length) throw InvalidExecutable();
        input.ReadExactly(dos);
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.Slice(60, 4));
        if (dos[0] != 'M' || dos[1] != 'Z' || peOffset < dos.Length || peOffset > input.Length - 24)
            throw InvalidExecutable();
        input.Position = peOffset;
        Span<byte> header = stackalloc byte[24];
        input.ReadExactly(header);
        ushort sections = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        ushort optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(20, 2));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(22, 2));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00004550
            || BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2)) != expectedMachine
            || (characteristics & 0x0002) == 0 || (characteristics & 0x2000) != 0
            || sections is < 1 or > 96 || optionalSize < 112
            || peOffset + 24L + optionalSize + sections * 40L > input.Length)
            throw InvalidExecutable();
        Span<byte> magic = stackalloc byte[2];
        input.ReadExactly(magic);
        if (BinaryPrimitives.ReadUInt16LittleEndian(magic) != 0x020B) throw InvalidExecutable();
    }

    private static InvalidDataException InvalidExecutable() => new("The downloaded file is not a valid application for this architecture.");
}
