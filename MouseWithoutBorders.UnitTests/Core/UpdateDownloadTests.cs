// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class UpdateDownloadTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string ApplicationPath => Path.Combine(AppContext.BaseDirectory, "MouseWithoutBorders.exe");

    [TestMethod]
    public void ChecksumParserAcceptsOnlyTheNamedApplicationAndConventionalMarkers()
    {
        string digest = new('a', 64);
        foreach (string text in new[]
        {
            digest + "  MouseWithoutBorders.exe", digest + " *MouseWithoutBorders.exe\n",
            "\uFEFF" + digest + "\tMouseWithoutBorders.exe\r\n", digest + " MouseWithoutBorders.exe\r",
        })
            Assert.AreEqual(digest.ToUpperInvariant(), UpdateDownload.ParseChecksum(text));
    }

    [TestMethod]
    public void ChecksumParserRejectsBareHashesOtherNamesAndAdditionalLines()
    {
        string digest = new('a', 64);
        foreach (string text in new[]
        {
            "", digest, digest + " other.exe", digest + " ../MouseWithoutBorders.exe",
            digest + "  MouseWithoutBorders.exe\n" + digest + " other.exe\n",
            " " + digest + " MouseWithoutBorders.exe", digest + " MouseWithoutBorders.exe ",
            digest + " MouseWithoutBorders.exe\n\n", digest + " MouseWithoutBorders.exe\0",
            new string('g', 64) + " MouseWithoutBorders.exe", digest + " MouseWithoutBorders.EXE",
        })
            Assert.ThrowsException<InvalidDataException>(() => UpdateDownload.ParseChecksum(text), text);
    }

    [TestMethod]
    public void RedirectAllowlistRejectsAlternateHostsCredentialsAndTransportDowngrades()
    {
        foreach (string value in new[]
        {
            "https://github.com/path", "https://release-assets.githubusercontent.com/path?token=opaque",
            "https://objects.githubusercontent.com/path",
        })
            Assert.IsTrue(UpdateDownload.IsAllowedDownloadUri(new Uri(value)), value);
        foreach (string value in new[]
        {
            "http://github.com/path", "ftp://github.com/path", "https://github.com:444/path",
            "https://user:secret@github.com/path", "https://github.com/path#fragment",
            "https://sub.github.com/path", "https://github.com.attacker.invalid/path", "https://127.0.0.1/path",
            "https://release-assets.githubusercontent.com.attacker.invalid/path",
        })
            Assert.IsFalse(UpdateDownload.IsAllowedDownloadUri(new Uri(value)), value);
        Assert.IsFalse(UpdateDownload.IsAllowedDownloadUri(new Uri("relative", UriKind.Relative)));
    }

    [TestMethod]
    public void PeValidationAcceptsBothSupported64BitMachineTypesAndRejectsTheOpposite()
    {
        using var fixture = new DownloadFixture();
        string image = Path.Combine(fixture.Root, "fixture.exe");
        File.WriteAllBytes(image, PeImage(0x8664));
        UpdateDownload.ValidatePortableExecutable(image, Architecture.X64);
        Assert.ThrowsException<InvalidDataException>(() => UpdateDownload.ValidatePortableExecutable(image, Architecture.Arm64));
        File.WriteAllBytes(image, PeImage(0xAA64));
        UpdateDownload.ValidatePortableExecutable(image, Architecture.Arm64);
        Assert.ThrowsException<InvalidDataException>(() => UpdateDownload.ValidatePortableExecutable(image, Architecture.X64));
        Assert.ThrowsException<PlatformNotSupportedException>(() => UpdateDownload.ValidatePortableExecutable(image, Architecture.X86));
    }

    [TestMethod]
    public void PeValidationRejectsMalformedHeadersDllsTruncationAndNonExecutableImages()
    {
        using var fixture = new DownloadFixture();
        string image = Path.Combine(fixture.Root, "fixture.exe");
        var corruptions = new Action<byte[]>[]
        {
            bytes => bytes[0] = 0,
            bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), -1),
            bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), int.MaxValue),
            bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), bytes.Length - 20),
            bytes => bytes[128] = 0,
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), 0x014C),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(134), 0),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(134), 97),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148), 0),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148), ushort.MaxValue),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0x2002),
            bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(152), 0x010B),
        };
        foreach (var corrupt in corruptions)
        {
            byte[] bytes = PeImage(0x8664);
            corrupt(bytes);
            File.WriteAllBytes(image, bytes);
            Assert.ThrowsException<InvalidDataException>(() => UpdateDownload.ValidatePortableExecutable(image, Architecture.X64));
        }
        File.WriteAllBytes(image, new byte[20]);
        Assert.ThrowsException<InvalidDataException>(() => UpdateDownload.ValidatePortableExecutable(image, Architecture.X64));
    }

    [TestMethod]
    public void ProductVersionMustMatchTheReleaseWhileBuildMetadataIsIgnored()
    {
        string version = FileVersionInfo.GetVersionInfo(ApplicationPath).ProductVersion!;
        Assert.IsFalse(string.IsNullOrEmpty(version));
        UpdateDownload.ValidateExecutable(ApplicationPath, version.Split('+')[0] + "+different.build");
        Assert.ThrowsException<InvalidDataException>(() => UpdateDownload.ValidateExecutable(ApplicationPath, "99.98.97-preview.1"));
    }

    [TestMethod]
    public async Task CompleteDownloadChecksTheRealApplicationAndUsesPrivateStaging()
    {
        using var fixture = DownloadFixture.Application();
        var events = new List<UpdateDownloadProgress>();
        using var http = fixture.Client();
        string path;
        using (var downloaded = await UpdateDownload.DownloadAsync(fixture.Release,
            new ImmediateProgress(events.Add), CancellationToken.None, http, fixture.Root))
        {
            path = downloaded.ExecutablePath;
            Assert.AreEqual(Hash(fixture.ExecutableBytes), downloaded.Sha256);
            CollectionAssert.AreEqual(fixture.ExecutableBytes, File.ReadAllBytes(path));
            Assert.IsFalse(File.Exists(path + ".part"));
            Assert.AreEqual(0L, events.First().Received);
            Assert.AreEqual(fixture.Release.Executable.Size, events.Last().Received);
            Assert.IsTrue(events.All(value => value.Total == fixture.Release.Executable.Size && value.Received <= value.Total));
            AssertPrivate(new FileInfo(path).GetAccessControl());
            AssertPrivate(new DirectoryInfo(downloaded.DirectoryPath).GetAccessControl());
        }
        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.AreEqual(2, fixture.Requests.Count);
    }

    [TestMethod]
    public async Task PublishedChecksumStillVerifiesAssetsWhoseOptionalGithubDigestIsAbsent()
    {
        using var fixture = DownloadFixture.Application();
        var release = fixture.Release with
        {
            Executable = fixture.Release.Executable with { Sha256Digest = null! },
            Checksum = fixture.Release.Checksum with { Sha256Digest = null! },
        };
        using var http = fixture.Client();
        using var download = await UpdateDownload.DownloadAsync(release, null!, CancellationToken.None, http, fixture.Root);
        Assert.AreEqual(Hash(fixture.ExecutableBytes), download.Sha256);
    }

    [TestMethod]
    public async Task ChecksumGithubDigestMismatchStopsBeforeDownloadingTheExecutable()
    {
        using var fixture = new DownloadFixture();
        var release = fixture.Release with { Checksum = fixture.Release.Checksum with { Sha256Digest = new string('0', 64) } };
        using var http = fixture.Client();
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(release, null!, CancellationToken.None, http, fixture.Root));
        Assert.AreEqual(1, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task ExecutableGithubDigestDisagreementStopsBeforeDownloadingTheExecutable()
    {
        using var fixture = new DownloadFixture();
        var release = fixture.Release with { Executable = fixture.Release.Executable with { Sha256Digest = new string('0', 64) } };
        using var http = fixture.Client();
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(release, null!, CancellationToken.None, http, fixture.Root));
        Assert.AreEqual(1, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task CorruptExecutableBytesAreDeletedEvenWhenTheSizeIsCorrect()
    {
        using var fixture = new DownloadFixture();
        byte[] corrupt = (byte[])fixture.ExecutableBytes.Clone();
        corrupt[^1] ^= 1;
        using var http = fixture.Client((request, _) => Task.FromResult(fixture.Response(request, corrupt)));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task ExecutableStreamCannotExceedItsDeclaredSizeWithoutContentLength()
    {
        using var fixture = new DownloadFixture();
        byte[] oversized = fixture.ExecutableBytes.Concat(new byte[] { 1 }).ToArray();
        using var http = fixture.Client((request, _) => Task.FromResult(fixture.Response(request, oversized, chunked: true)));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task ShortExecutableStreamIsRejectedWithoutContentLength()
    {
        using var fixture = new DownloadFixture();
        using var http = fixture.Client((request, _) => Task.FromResult(fixture.Response(request, fixture.ExecutableBytes[..^1], chunked: true)));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task ContentLengthMustMatchReleaseMetadataBeforeBytesAreStaged()
    {
        using var fixture = new DownloadFixture();
        using var http = fixture.Client((request, _) =>
        {
            var response = fixture.Response(request);
            response.Content.Headers.ContentLength = request.RequestUri == fixture.Release.Checksum.DownloadUri
                ? fixture.ChecksumBytes.Length : fixture.ExecutableBytes.Length + 1;
            return Task.FromResult(response);
        });
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task OversizedChecksumStreamIsRejectedBeforeTheApplicationRequest()
    {
        using var fixture = new DownloadFixture();
        using var http = fixture.Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[ManualUpdates.MaximumChecksumSize + 1])) }));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        Assert.AreEqual(1, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task InvalidChecksumFilenameIsRejectedBeforeTheApplicationRequest()
    {
        using var fixture = new DownloadFixture();
        byte[] checksum = Encoding.UTF8.GetBytes(Hash(fixture.ExecutableBytes) + "  different-application.exe\n");
        var release = fixture.Release with { Checksum = fixture.Release.Checksum with { Size = checksum.Length, Sha256Digest = Hash(checksum) } };
        using var http = fixture.Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(checksum) }));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(release, null!, CancellationToken.None, http, fixture.Root));
        Assert.AreEqual(1, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task PreCancelledRequestDoesNotReachTheNetworkOrCreateStaging()
    {
        using var fixture = new DownloadFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = fixture.Client();
        await Throws<OperationCanceledException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, cancellation.Token, http, fixture.Root));
        Assert.AreEqual(0, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task CancellationOfTheChecksumRequestIsForwarded()
    {
        using var fixture = new DownloadFixture();
        using var cancellation = new CancellationTokenSource();
        using var http = fixture.Client(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new AssertFailedException("A cancelled request must not finish.");
        });
        await Throws<OperationCanceledException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, cancellation.Token, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task CancellationDuringExecutableStreamingRemovesThePartialFile()
    {
        using var fixture = new DownloadFixture();
        using var cancellation = new CancellationTokenSource();
        using var http = fixture.Client((request, _) => Task.FromResult(fixture.Response(request, chunked: true)));
        var progress = new ImmediateProgress(value => { if (value.Received > 0) cancellation.Cancel(); });
        await Throws<OperationCanceledException>(() => UpdateDownload.DownloadAsync(fixture.Release, progress, cancellation.Token, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task GithubCdnRedirectsAreFollowedWithoutCredentialsOrCookies()
    {
        using var fixture = DownloadFixture.Application();
        var checksumUri = new Uri("https://objects.githubusercontent.com/checksum?token=example");
        var executableUri = new Uri("https://release-assets.githubusercontent.com/application?token=example");
        using var http = fixture.Client((request, _) =>
        {
            Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            if (request.RequestUri == fixture.Release.Checksum.DownloadUri) return Task.FromResult(Redirect(checksumUri));
            if (request.RequestUri == fixture.Release.Executable.DownloadUri) return Task.FromResult(Redirect(executableUri));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri == checksumUri ? fixture.ChecksumBytes : fixture.ExecutableBytes) });
        });
        using var downloaded = await UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root);
        Assert.AreEqual(4, fixture.Requests.Count);
    }

    [TestMethod]
    public async Task UnsupportedRedirectIsRejectedBeforeSendingAnotherRequest()
    {
        foreach (string target in new[] { "http://github.com/file", "https://example.invalid/file", "https://user@github.com/file", "https://github.com/file#part" })
        {
            using var fixture = new DownloadFixture();
            using var http = fixture.Client((_, _) => Task.FromResult(Redirect(new Uri(target))));
            await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
            Assert.AreEqual(1, fixture.Requests.Count);
            AssertNoStages(fixture);
        }
    }

    [TestMethod]
    public async Task RedirectLoopsStopAfterFiveRedirects()
    {
        using var fixture = new DownloadFixture();
        using var http = fixture.Client((request, _) => Task.FromResult(Redirect(request.RequestUri!)));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        Assert.AreEqual(6, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task InvalidOrCrossRepositoryAssetMetadataIsRejectedBeforeAnyRequest()
    {
        using var fixture = new DownloadFixture();
        foreach (var release in new[]
        {
            fixture.Release with { Executable = fixture.Release.Executable with { Name = "../MouseWithoutBorders.exe" } },
            fixture.Release with { Executable = fixture.Release.Executable with { Size = 0 } },
            fixture.Release with { Executable = fixture.Release.Executable with { Size = ManualUpdates.MaximumExecutableSize + 1 } },
            fixture.Release with { Checksum = fixture.Release.Checksum with { Size = ManualUpdates.MaximumChecksumSize + 1 } },
            fixture.Release with { Executable = fixture.Release.Executable with { DownloadUri = new Uri("https://github.com/another/repository/releases/download/mwb-v1.2.3/MouseWithoutBorders.exe") } },
            fixture.Release with { Executable = fixture.Release.Executable with { Sha256Digest = "not-a-digest" } },
            fixture.Release with { Tag = "mwb-v../invalid" },
        })
        {
            using var http = fixture.Client();
            await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(release, null!, CancellationToken.None, http, fixture.Root));
        }
        Assert.AreEqual(0, fixture.Requests.Count);
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task HttpFailureLeavesNoStagedApplication()
    {
        using var fixture = new DownloadFixture();
        using var http = fixture.Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Throws<HttpRequestException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public async Task ValidHashDoesNotPermitAnImageWithTheWrongEmbeddedVersion()
    {
        using var fixture = DownloadFixture.Application();
        var release = fixture.ForVersion("99.98.97-preview.1");
        using var http = fixture.Client((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? fixture.ChecksumBytes : fixture.ExecutableBytes) }));
        await Throws<InvalidDataException>(() => UpdateDownload.DownloadAsync(release, null!, CancellationToken.None, http, fixture.Root));
        AssertNoStages(fixture);
    }

    [TestMethod]
    public void DisposeRemovesOnlyKnownFilesAndPreservesUnrelatedContent()
    {
        using var fixture = new DownloadFixture();
        string owned = Path.Combine(fixture.Root, "owned");
        Directory.CreateDirectory(owned);
        string unrelated = Path.Combine(owned, "keep.txt");
        string sibling = Path.Combine(fixture.Root, "sibling.txt");
        File.WriteAllText(unrelated, "keep");
        File.WriteAllText(sibling, "keep");
        var downloaded = new DownloadedUpdate(owned, new string('A', 64));
        File.WriteAllText(downloaded.ExecutablePath, "owned");
        File.WriteAllText(downloaded.PartialPath, "owned");
        downloaded.Dispose();
        downloaded.Dispose();
        Assert.IsFalse(File.Exists(downloaded.ExecutablePath));
        Assert.IsFalse(File.Exists(downloaded.PartialPath));
        Assert.AreEqual("keep", File.ReadAllText(unrelated));
        Assert.AreEqual("keep", File.ReadAllText(sibling));
    }

    [TestMethod]
    public async Task DownloadRejectsAncestorsThatOtherPrincipalsCanModify()
    {
        using var fixture = new DownloadFixture();
        var directory = new DirectoryInfo(fixture.Root);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        directory.SetAccessControl(security);
        using var http = fixture.Client();
        await Throws<SecurityException>(() => UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None,
            http, Path.Combine(fixture.Root, "download-root")));
        Assert.AreEqual(1, fixture.Requests.Count, "Only the small checksum is fetched before creating the download stage.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "download-root")));
    }

    [TestMethod]
    public async Task DisposeRetainsFilesIfTheOwnedStageBecameUnsafe()
    {
        using var fixture = DownloadFixture.Application();
        using var http = fixture.Client();
        using var downloaded = await UpdateDownload.DownloadAsync(fixture.Release, null!, CancellationToken.None, http, fixture.Root);
        var directory = new DirectoryInfo(downloaded.DirectoryPath);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.WriteData, AccessControlType.Allow));
        directory.SetAccessControl(security);
        downloaded.Dispose();
        Assert.IsTrue(File.Exists(downloaded.ExecutablePath), "Cleanup must not follow a path whose trust boundary changed.");
    }

    private static void AssertPrivate(FileSystemSecurity security)
    {
        Assert.IsTrue(security.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var permitted = new HashSet<string>
        {
            identity.User!.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        };
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.IsTrue(rules.Length >= 2);
        foreach (var rule in rules)
            Assert.IsTrue(permitted.Contains(rule.IdentityReference.Value), "Unexpected principal in private update ACL.");
    }

    private static void AssertNoStages(DownloadFixture fixture) => Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.Root).Length);

    private static byte[] PeImage(ushort machine)
    {
        byte[] bytes = new byte[512];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(134), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148), 112);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0x0002);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(152), 0x020B);
        return bytes;
    }

    private static HttpResponseMessage Redirect(Uri target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = target;
        return response;
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        Assert.Fail("Expected " + typeof(T).Name);
        throw new InvalidOperationException();
    }

    private sealed class ImmediateProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }

    private sealed class DownloadFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "MouseWithoutBorders.UpdateDownloadTests", Guid.NewGuid().ToString("N"));
        internal byte[] ExecutableBytes { get; }
        internal byte[] ChecksumBytes { get; }
        internal UpdateRelease Release { get; }
        internal List<Uri> Requests { get; } = new();

        internal DownloadFixture(byte[]? executable = null, string version = "1.2.3-preview.4")
        {
            Directory.CreateDirectory(Root);
            ExecutableBytes = executable ?? PeImage(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664);
            ChecksumBytes = Encoding.UTF8.GetBytes(Hash(ExecutableBytes) + "  MouseWithoutBorders.exe\n");
            Release = ForVersion(version);
        }

        internal static DownloadFixture Application() => new(File.ReadAllBytes(ApplicationPath), FileVersionInfo.GetVersionInfo(ApplicationPath).ProductVersion!);

        internal UpdateRelease ForVersion(string version)
        {
            string tag = "mwb-v" + version;
            string download = ManualUpdates.Releases + "/download/" + Uri.EscapeDataString(tag) + "/";
            return new UpdateRelease(tag, version, version.Contains('-'), new Uri(ManualUpdates.Releases + "/tag/" + tag),
                new UpdateAsset("MouseWithoutBorders.exe", ExecutableBytes.Length, new Uri(download + "MouseWithoutBorders.exe"), Hash(ExecutableBytes)),
                new UpdateAsset("MouseWithoutBorders.exe.sha256", ChecksumBytes.Length, new Uri(download + "MouseWithoutBorders.exe.sha256"), Hash(ChecksumBytes)));
        }

        internal HttpResponseMessage Response(HttpRequestMessage request, byte[]? executable = null, bool chunked = false)
        {
            byte[] bytes = request.RequestUri == Release.Checksum.DownloadUri ? ChecksumBytes : executable ?? ExecutableBytes;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = chunked ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes) };
        }

        internal HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? reply = null) => new(new FakeHandler(async (request, token) =>
        {
            Requests.Add(request.RequestUri!);
            return reply == null ? Response(request) : await reply(request, token);
        })) { Timeout = Timeout.InfiniteTimeSpan };

        public void Dispose()
        {
            string expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "MouseWithoutBorders.UpdateDownloadTests")) + Path.DirectorySeparatorChar;
            string absolute = Path.GetFullPath(Root);
            if (!absolute.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(count, 4096), bytes.Length - position);
            bytes.AsSpan(position, take).CopyTo(buffer.AsSpan(offset, take));
            position += take;
            return take;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(Math.Min(buffer.Length, 4096), bytes.Length - position);
            bytes.AsMemory(position, take).CopyTo(buffer);
            position += take;
            return ValueTask.FromResult(take);
        }
    }
}
