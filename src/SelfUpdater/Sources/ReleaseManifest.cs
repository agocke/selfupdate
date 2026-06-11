using Serde;

namespace SelfUpdater.Sources;

/// <summary>
/// The JSON manifest shape shared by <see cref="HttpManifestUpdateSource"/> and
/// <see cref="DirectoryUpdateSource"/>. A manifest lists one or more releases, each
/// with its assets keyed by a name the publisher chooses (typically a runtime
/// identifier, but the key is opaque to the engine — the consumer matches it):
/// <code>
/// {
///   "releases": [
///     {
///       "version": "0.2.0",
///       "prerelease": false,
///       "assets": {
///         "osx-arm64": { "url": "myapp-osx-arm64", "sha256": "..." },
///         "linux-x64": { "url": "https://.../myapp-linux-x64" }
///       }
///     }
///   ]
/// }
/// </code>
/// Each asset's <c>url</c> is interpreted by the source: an absolute URL for the
/// HTTP source, a path relative to the manifest's directory for the file source.
/// Listing multiple releases lets a consumer pin, skip, or roll back rather than
/// always taking "latest".
/// </summary>
[GenerateSerde]
public sealed partial record ReleaseManifest
{
    public required List<ManifestRelease> Releases { get; init; }
}

[GenerateSerde]
public sealed partial record ManifestRelease
{
    public required string Version { get; init; }

    /// <summary>Marks this release as a prerelease so consumers can filter it out.</summary>
    public bool? Prerelease { get; init; }

    public required Dictionary<string, ManifestAsset> Assets { get; init; }
}

[GenerateSerde]
public sealed partial record ManifestAsset
{
    public required string Url { get; init; }
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
}

internal static class ManifestMapping
{
    /// <summary>
    /// Map a parsed manifest to <see cref="UpdateRelease"/>s, using
    /// <paramref name="resolveLocation"/> to turn each asset's <c>url</c> into the
    /// opaque <see cref="UpdateAsset.Location"/> the source can later open. Each
    /// asset's dictionary key becomes its <see cref="UpdateAsset.Name"/>. Releases
    /// whose version does not parse are skipped.
    /// </summary>
    public static IReadOnlyList<UpdateRelease> ToReleases(ReleaseManifest manifest, Func<string, string> resolveLocation)
    {
        var result = new List<UpdateRelease>(manifest.Releases.Count);
        foreach (var release in manifest.Releases)
        {
            if (!SemVer.TryParse(release.Version, out var version))
                continue;

            var assets = new List<UpdateAsset>(release.Assets.Count);
            foreach (var (name, a) in release.Assets)
                assets.Add(new UpdateAsset(name, resolveLocation(a.Url), a.Sha256, a.Size));

            result.Add(new UpdateRelease(version, assets, (release.Prerelease ?? false) || version.IsPrerelease));
        }
        return result;
    }
}
