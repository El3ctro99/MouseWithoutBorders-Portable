// Copyright (c) Mouse Without Borders Portable contributors
// Licensed under the MIT license.
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MouseWithoutBorders.Core;

internal sealed record UpdateAsset(string Name, long Size, Uri DownloadUri, string Sha256Digest);

internal sealed record UpdateRelease(string Tag, string Version, bool IsPrerelease, Uri ReleasePage, UpdateAsset Executable, UpdateAsset Checksum);

internal static class ManualUpdates
{
    internal const string Releases = "https://github.com/El3ctro99/MouseWithoutBorders-Portable/releases";
    internal const string ReleasesApi = "https://api.github.com/repos/El3ctro99/MouseWithoutBorders-Portable/releases?per_page=100";
    internal const long MaximumExecutableSize = 256L * 1024 * 1024;
    internal const int MaximumChecksumSize = 4096;
    internal const int MaximumMetadataSize = 1024 * 1024;
    private const string TagPrefix = "mwb-v";

    // Retained for the existing version-only check and its historical fixtures.
    internal static async Task<string> Check(string current, CancellationToken token)
    {
        string json = await FetchMetadataAsync(current, token).ConfigureAwait(false);
        return SelectNewer(json, current);
    }

    internal static async Task<UpdateRelease> CheckCandidateAsync(string current, CancellationToken token)
    {
        string json = await FetchMetadataAsync(current, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return SelectCandidate(json, current);
    }

    private static async Task<string> FetchMetadataAsync(string current, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ParseInstalled(current);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = MaximumMetadataSize };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MouseWithoutBorders-Portable/" + current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return await http.GetStringAsync(ReleasesApi, token).ConfigureAwait(false);
    }

    internal static string SelectNewer(string json, string current) => FindNewer(json, current)?.Tag;

    internal static UpdateRelease SelectCandidate(string json, string current)
    {
        var selected = FindNewer(json, current);
        if (selected == null)
        {
            return null;
        }

        // Do not silently fall back to an older release if the newest release is incomplete.
        if (selected.Metadata["assets"] is not JArray assets || assets.Count > 128)
        {
            throw InvalidAssets();
        }

        UpdateAsset executable = ReadAsset(assets, selected.Tag, "MouseWithoutBorders.exe", MaximumExecutableSize);
        UpdateAsset checksum = ReadAsset(assets, selected.Tag, "MouseWithoutBorders.exe.sha256", MaximumChecksumSize);
        return new UpdateRelease(selected.Tag, selected.Version.Text, selected.Version.IsPrerelease,
            new Uri(Releases + "/tag/" + Uri.EscapeDataString(selected.Tag)), executable, checksum);
    }

    private static UpdateAsset ReadAsset(JArray assets, string tag, string name, long maximumSize)
    {
        var matches = assets.OfType<JObject>().Where(asset => ReadString(asset["name"]) == name).ToArray();
        if (matches.Length != 1)
        {
            throw InvalidAssets();
        }

        JObject metadata = matches[0];
        if (metadata["size"]?.Type != JTokenType.Integer || !long.TryParse(metadata["size"].ToString(), out long size)
            || size < 1 || size > maximumSize || ReadString(metadata["state"]) != "uploaded")
        {
            throw InvalidAssets();
        }

        string expected = Releases + "/download/" + Uri.EscapeDataString(tag) + "/" + name;
        string download = ReadString(metadata["browser_download_url"]);
        // Exact matching also rejects credentials, query strings, fragments, other repositories,
        // encoded path separators, alternate ports, and same-name assets from a different tag.
        if (!string.Equals(download, expected, StringComparison.Ordinal) || !Uri.TryCreate(download, UriKind.Absolute, out var uri))
        {
            throw InvalidAssets();
        }

        string digest = null;
        JToken digestMetadata = metadata["digest"];
        if (digestMetadata != null && digestMetadata.Type != JTokenType.Null)
        {
            string value = ReadString(digestMetadata);
            if (value == null || !value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 71
                || !value.Substring(7).All(IsHex))
            {
                throw InvalidAssets();
            }

            digest = value.Substring(7).ToUpperInvariant();
        }

        return new UpdateAsset(name, size, uri, digest);
    }

    private static InvalidDataException InvalidAssets() => new("The newest release does not contain valid application and SHA-256 downloads. Open GitHub to check the release.");

    private static ReleaseMetadata FindNewer(string json, string current)
    {
        SemanticVersion installed = ParseInstalled(current);
        JArray releases = ReadReleases(json);
        ReleaseMetadata newest = null;
        foreach (var release in releases.OfType<JObject>())
        {
            string tag = ReadString(release["tag_name"]);
            if (release["draft"]?.Type != JTokenType.Boolean || release["draft"].Value<bool>()
                || release["prerelease"]?.Type != JTokenType.Boolean || tag == null
                || !tag.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = SemanticVersion.Parse(tag.Substring(TagPrefix.Length));
            if (parsed == null || release["prerelease"].Value<bool>() != parsed.IsPrerelease
                || (!installed.IsPrerelease && parsed.IsPrerelease) || parsed.CompareTo(installed) <= 0)
            {
                continue;
            }

            if (newest == null || parsed.CompareTo(newest.Version) > 0
                || (parsed.CompareTo(newest.Version) == 0 && string.CompareOrdinal(tag, newest.Tag) > 0))
            {
                newest = new ReleaseMetadata(tag, parsed, release);
            }
        }

        return newest;
    }

    private static SemanticVersion ParseInstalled(string current) => SemanticVersion.Parse(current)
        ?? throw new InvalidOperationException("This app version cannot be compared. Open GitHub to check manually.");

    private static JArray ReadReleases(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumMetadataSize)
        {
            throw new InvalidDataException("GitHub returned invalid or oversized release metadata.");
        }

        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
            var result = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (result is not JArray releases || releases.Count > 100 || reader.Read())
            {
                throw new InvalidDataException("GitHub returned invalid release metadata.");
            }

            return releases;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("GitHub returned invalid release metadata.", error);
        }
    }

    private static string ReadString(JToken token) => token?.Type == JTokenType.String ? token.Value<string>() : null;
    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private sealed record ReleaseMetadata(string Tag, SemanticVersion Version, JObject Metadata);

    // SemVer 2.0 precedence, bounded to 128 ASCII characters. Numeric identifiers are
    // compared as digit strings so malformed/huge numbers cannot overflow integer parsing.
    private sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private readonly string[] core;
        private readonly string[] prerelease;
        internal string Text { get; }
        internal bool IsPrerelease => prerelease.Length != 0;

        private SemanticVersion(string text, string[] core, string[] prerelease)
        {
            Text = text;
            this.core = core;
            this.prerelease = prerelease;
        }

        internal static SemanticVersion Parse(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                return null;
            }

            string[] withBuild = value.Split('+');
            if (withBuild.Length > 2 || (withBuild.Length == 2 && !ValidIdentifiers(withBuild[1].Split('.'), false)))
            {
                return null;
            }

            int separator = withBuild[0].IndexOf('-');
            string[] core = (separator < 0 ? withBuild[0] : withBuild[0].Substring(0, separator)).Split('.');
            string[] prerelease = separator < 0 ? Array.Empty<string>() : withBuild[0].Substring(separator + 1).Split('.');
            if (core.Length != 3 || core.Any(part => !IsNumeric(part) || HasLeadingZero(part))
                || (separator >= 0 && !ValidIdentifiers(prerelease, true)))
            {
                return null;
            }

            return new SemanticVersion(value, core, prerelease);
        }

        private static bool ValidIdentifiers(string[] parts, bool rejectLeadingZero) => parts.All(part => part.Length != 0
            && part.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-')
            && (!rejectLeadingZero || !IsNumeric(part) || !HasLeadingZero(part)));
        private static bool IsNumeric(string value) => value.Length != 0 && value.All(character => character is >= '0' and <= '9');
        private static bool HasLeadingZero(string value) => value.Length > 1 && value[0] == '0';
        private static int CompareNumeric(string left, string right) => left.Length != right.Length
            ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

        public int CompareTo(SemanticVersion other)
        {
            for (int index = 0; index < core.Length; index++)
            {
                int compared = CompareNumeric(core[index], other.core[index]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            if (IsPrerelease != other.IsPrerelease)
            {
                return IsPrerelease ? -1 : 1;
            }

            for (int index = 0; index < Math.Min(prerelease.Length, other.prerelease.Length); index++)
            {
                string left = prerelease[index], right = other.prerelease[index];
                bool leftNumeric = IsNumeric(left), rightNumeric = IsNumeric(right);
                int compared = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                    : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return prerelease.Length.CompareTo(other.prerelease.Length);
        }
    }
}
