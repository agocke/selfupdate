using Serde;

namespace SelfUpdater.Sources;

/// <summary>
/// The JSON manifest shape shared by <see cref="HttpManifestUpdateSource"/> and
/// <see cref="DirectoryUpdateSource"/>:
/// <code>
/// {
///   "latestVersion": {
///     "version": "0.2.0",
///     "assets": {
///       "osx-arm64": { "url": "bowerd-osx-arm64", "sha256": "..." },
///       "linux-x64": { "url": "https://.../bowerd-linux-x64" }
///     }
///   }
/// }
/// </code>
/// Each asset's <c>url</c> is interpreted by the source: an absolute URL for the
/// HTTP source, a path relative to the manifest's directory for the file source.
/// </summary>
[GenerateSerde]
public sealed partial record ReleaseManifest
{
    public required ManifestRelease LatestVersion { get; init; }
}

[GenerateSerde]
public sealed partial record ManifestRelease
{
    public required string Version { get; init; }
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
    /// Map a parsed manifest to an <see cref="UpdateRelease"/> for the given RID,
    /// using <paramref name="resolveLocation"/> to turn an asset's <c>url</c> into
    /// the opaque <see cref="UpdateAsset.Location"/> the source can later open.
    /// </summary>
    public static UpdateRelease? ToRelease(ReleaseManifest manifest, string rid, Func<string, string> resolveLocation)
    {
        if (!SemVer.TryParse(manifest.LatestVersion.Version, out var version))
            return null;

        UpdateAsset? asset = null;
        if (manifest.LatestVersion.Assets.TryGetValue(rid, out var a))
            asset = new UpdateAsset(rid, resolveLocation(a.Url), a.Sha256, a.Size);

        return new UpdateRelease(version, asset);
    }
}
