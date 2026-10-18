// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MouseWithoutBorders.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MouseWithoutBorders.UnitTests.Core;

[TestClass]
public sealed class ManualUpdateReleaseTests
{
    [TestMethod]
    public void StableUsersOnlySeeNewerStableReleases()
    {
        var releases = new JArray(Release("1.3.0-preview.20"), Release("1.2.0"), Release("1.1.2"), Release("1.2.0-rc.10"));
        Assert.AreEqual("mwb-v1.2.0", ManualUpdates.SelectCandidate(Json(releases), "1.1.1").Tag);
        Assert.IsNull(ManualUpdates.SelectCandidate(Json(releases), "1.2.0"));
    }

    [TestMethod]
    public void PreviewUsersCanAdvanceToLaterPreviewReleaseCandidateAndStable()
    {
        var releases = new JArray(Release("1.2.0-preview.9"), Release("1.2.0-preview.10"));
        Assert.AreEqual("mwb-v1.2.0-preview.10", ManualUpdates.SelectCandidate(Json(releases), "1.2.0-preview.3").Tag);
        releases.Add(Release("1.2.0-rc.2"));
        Assert.AreEqual("mwb-v1.2.0-rc.2", ManualUpdates.SelectCandidate(Json(releases), "1.2.0-preview.10").Tag);
        releases.Add(Release("1.2.0"));
        Assert.AreEqual("mwb-v1.2.0", ManualUpdates.SelectCandidate(Json(releases), "1.2.0-rc.2").Tag);
    }

    [TestMethod]
    public void SemVerComparesNumericIdentifiersWithoutIntegerOverflowAndIgnoresBuildMetadata()
    {
        var releases = new JArray(Release("1.2.0-preview.99999999999999999999"), Release("1.2.0-preview.100000000000000000000"));
        Assert.AreEqual("mwb-v1.2.0-preview.100000000000000000000", ManualUpdates.SelectNewer(Json(releases), "1.2.0-preview.9"));
        Assert.IsNull(ManualUpdates.SelectNewer(Json(new JArray(Release("1.2.0-preview.3+different.001"))), "1.2.0-preview.3+build.5"));
        Assert.AreEqual("mwb-v100000000000000000000.0.0", ManualUpdates.SelectNewer(Json(new JArray(Release("100000000000000000000.0.0"))), "99999999999999999999.0.0"));
    }

    [TestMethod]
    public void SemVerIdentifierPrecedenceMatchesTheSpecification()
    {
        string[] versions = { "1.2.0-alpha", "1.2.0-alpha.1", "1.2.0-alpha.beta", "1.2.0-beta", "1.2.0-beta.2", "1.2.0-beta.11", "1.2.0-rc.1", "1.2.0" };
        for (int index = 1; index < versions.Length; index++)
        {
            Assert.AreEqual("mwb-v" + versions[index], ManualUpdates.SelectNewer(Json(new JArray(Release(versions[index]))), versions[index - 1]));
            Assert.IsNull(ManualUpdates.SelectNewer(Json(new JArray(Release(versions[index - 1]))), versions[index]));
        }
    }

    [DataTestMethod]
    [DataRow("mwb-v01.2.0")]
    [DataRow("mwb-v1.02.0")]
    [DataRow("mwb-v1.2.00")]
    [DataRow("mwb-v1.2.0-preview.01")]
    [DataRow("mwb-v1.2.0-preview..3")]
    [DataRow("mwb-v1.2.0+")]
    [DataRow("mwb-v1.2.0+build..2")]
    [DataRow("mwb-v1.2.0-preview.3+build+extra")]
    [DataRow("mwb-v1.2.0-preview.３")]
    [DataRow("1.2.0")]
    [DataRow("prefix-mwb-v1.2.0")]
    [DataRow("mwb-v1.2.0-mwb-vrc.10/")]
    [DataRow("mwb-v1.2.0\n")]
    public void InvalidOrUnanchoredTagsCannotBecomeAnUpdate(string tag)
    {
        var release = Release("1.2.0");
        release["tag_name"] = tag;
        release["prerelease"] = tag.Contains('-');
        Assert.IsNull(ManualUpdates.SelectNewer(Json(new JArray(release)), "1.1.0-preview.1"));
    }

    [TestMethod]
    public void DraftsMissingFlagsAndMismatchedPrereleaseFlagsAreIgnored()
    {
        var draft = Release("1.3.0"); draft["draft"] = true;
        var missing = Release("1.4.0"); missing.Remove("draft");
        var mismatched = Release("1.5.0-preview.1"); mismatched["prerelease"] = false;
        var stableMismatch = Release("1.6.0"); stableMismatch["prerelease"] = true;
        var stringFlag = Release("1.7.0"); stringFlag["draft"] = "false";
        Assert.AreEqual("mwb-v1.2.0", ManualUpdates.SelectNewer(Json(new JArray(draft, missing, mismatched, stableMismatch, stringFlag, Release("1.2.0"))), "1.1.0-preview.1"));
    }

    [TestMethod]
    public void CandidateContainsOnlyValidatedFixedRepositoryAssetMetadata()
    {
        var release = Release("1.2.0-preview.10+build.001");
        ((JArray)release["assets"]!)[0]!["digest"] = "sha256:" + new string('a', 64);
        var result = ManualUpdates.SelectCandidate(Json(new JArray(release)), "1.2.0-preview.3");
        Assert.AreEqual("1.2.0-preview.10+build.001", result.Version);
        Assert.IsTrue(result.IsPrerelease);
        Assert.AreEqual(ManualUpdates.Releases + "/tag/mwb-v1.2.0-preview.10%2Bbuild.001", result.ReleasePage.AbsoluteUri);
        Assert.AreEqual("MouseWithoutBorders.exe", result.Executable.Name);
        Assert.AreEqual(100L, result.Executable.Size);
        Assert.AreEqual(new string('A', 64), result.Executable.Sha256Digest);
        Assert.IsNull(result.Checksum.Sha256Digest);
        Assert.AreEqual(ManualUpdates.Releases + "/download/mwb-v1.2.0-preview.10%2Bbuild.001/MouseWithoutBorders.exe", result.Executable.DownloadUri.AbsoluteUri);
    }

    [TestMethod]
    public void NewestIncompleteReleaseFailsInsteadOfFallingBackToOlderReleaseOrReportingUpToDate()
    {
        var incomplete = Release("1.3.0"); incomplete.Remove("assets");
        string json = Json(new JArray(Release("1.2.0"), incomplete));
        Assert.AreEqual("mwb-v1.3.0", ManualUpdates.SelectNewer(json, "1.1.0"));
        Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(json, "1.1.0"));
        Assert.IsNull(ManualUpdates.SelectCandidate(json, "1.3.0"));
    }

    [TestMethod]
    public void BothExecutableAndChecksumAssetsAreRequiredAndDuplicatesFailClosed()
    {
        foreach (int index in new[] { 0, 1 })
        {
            var release = Release("1.2.0"); var assets = (JArray)release["assets"]!;
            assets.RemoveAt(index);
            Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(release)), "1.1.0"));
            release = Release("1.2.0"); assets = (JArray)release["assets"]!;
            assets.Add(assets[index]!.DeepClone());
            Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(release)), "1.1.0"));
        }
    }

    [DataTestMethod]
    [DataRow("http://github.com/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe")]
    [DataRow("https://github.com/aeae1/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe")]
    [DataRow("https://github.com.evil.invalid/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe")]
    [DataRow("https://user@github.com/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe")]
    [DataRow("https://github.com/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.1.0/MouseWithoutBorders.exe")]
    [DataRow("https://github.com/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe?download=1")]
    [DataRow("https://github.com/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe#part")]
    [DataRow("https://github.com:443/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0/MouseWithoutBorders.exe")]
    [DataRow("https://github.com/El3ctro99/MouseWithoutBorders-Portable/releases/download/mwb-v1.2.0%2f/MouseWithoutBorders.exe")]
    public void ForeignOrAmbiguousDownloadUrlsAreRejected(string url)
    {
        var release = Release("1.2.0");
        release["assets"]![0]!["browser_download_url"] = url;
        Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(release)), "1.1.0"));
    }

    [TestMethod]
    public void AssetsRequireUploadedStateAndBoundedIntegerSizes()
    {
        foreach (JToken invalidSize in new JToken[] { new JValue(0), new JValue(-1), new JValue(ManualUpdates.MaximumExecutableSize + 1), new JValue("100"), new JValue(100.5), new JValue(ulong.MaxValue) })
        {
            var release = Release("1.2.0"); release["assets"]![0]!["size"] = invalidSize;
            Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(release)), "1.1.0"));
        }
        var largeChecksum = Release("1.2.0"); largeChecksum["assets"]![1]!["size"] = ManualUpdates.MaximumChecksumSize + 1;
        Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(largeChecksum)), "1.1.0"));
        var pending = Release("1.2.0"); pending["assets"]![0]!["state"] = "starter";
        Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(pending)), "1.1.0"));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("sha256:bad")]
    [DataRow("sha512:0123456789012345678901234567890123456789012345678901234567890123")]
    [DataRow("SHA256:0123456789012345678901234567890123456789012345678901234567890123")]
    [DataRow("sha256:g123456789012345678901234567890123456789012345678901234567890123")]
    public void SuppliedInvalidGitHubDigestsFailClosed(string digest)
    {
        var release = Release("1.2.0"); release["assets"]![0]!["digest"] = digest;
        Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(release)), "1.1.0"));
    }

    [TestMethod]
    public void ReleaseMetadataIsBoundedAndRejectsMalformedAmbiguousOrTrailingJson()
    {
        foreach (string invalid in new[] { "", "{}", "[", "[] []", "[{\"draft\":true,\"draft\":false}]", new string(' ', ManualUpdates.MaximumMetadataSize + 1), new string('[', 20) + new string(']', 20) })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(invalid, "1.1.0"));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => ManualUpdates.SelectCandidate(Json(new JArray(Enumerable.Range(0, 101).Select(_ => Release("1.0.0")))), "1.1.0"));
    }

    [DataTestMethod]
    [DataRow("1.2")]
    [DataRow("1.02.0")]
    [DataRow("1.2.0-preview.01")]
    [DataRow("1.2.0+bad/metadata")]
    public void UncomparableInstalledVersionProducesExplicitFailure(string current)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => ManualUpdates.SelectCandidate("[]", current));
    }

    [TestMethod]
    public async Task AlreadyCancelledCheckDoesNotStartANetworkRequest()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ManualUpdates.CheckCandidateAsync("1.2.0-preview.3", cancellation.Token));
    }

    private static JObject Release(string version)
    {
        string tag = "mwb-v" + version;
        return new JObject
        {
            ["tag_name"] = tag,
            ["draft"] = false,
            ["prerelease"] = version.Split('+')[0].Contains('-'),
            ["assets"] = new JArray(Asset(tag, "MouseWithoutBorders.exe", 100), Asset(tag, "MouseWithoutBorders.exe.sha256", 90)),
        };
    }

    private static JObject Asset(string tag, string name, long size) => new()
    {
        ["name"] = name,
        ["size"] = size,
        ["state"] = "uploaded",
        ["browser_download_url"] = ManualUpdates.Releases + "/download/" + Uri.EscapeDataString(tag) + "/" + name,
        ["digest"] = null,
    };

    private static string Json(JArray releases) => releases.ToString(Formatting.None);
}
