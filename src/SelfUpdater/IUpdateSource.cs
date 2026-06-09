namespace SelfUpdater;

/// <summary>
/// A downloadable build artifact for a specific runtime identifier.
/// <see cref="Location"/> is opaque to the engine and interpreted only by the
/// <see cref="IUpdateSource"/> that produced it (a URL for the HTTP/GitHub
/// sources, but it could equally be a file path, blob id, or torrent magnet for
/// some future source).
/// </summary>
public sealed record UpdateAsset(string Rid, string Location, string? Sha256 = null, long? Size = null);

/// <summary>The newest release a source knows about, plus the asset for the requested RID (if any).</summary>
public sealed record UpdateRelease(SemVer Version, UpdateAsset? Asset);

/// <summary>
/// Pluggable strategy for discovering and fetching new builds. This is the seam
/// that keeps the updater independent of any particular distribution mechanism:
/// the engine only ever asks "what's the latest build for my RID?" and "give me
/// a stream of this asset's bytes". Concrete sources decide *where* builds live
/// and *how* they are described (a JSON manifest, a GitHub release, a package
/// feed, a LAN share, ...).
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// Resolve the latest available release and the asset matching <paramref name="rid"/>.
    /// Returns <c>null</c> if the source could not be reached or parsed. The
    /// release's <see cref="UpdateRelease.Asset"/> may be <c>null</c> when a newer
    /// version exists but ships nothing for this platform.
    /// </summary>
    Task<UpdateRelease?> GetLatestReleaseAsync(string rid, CancellationToken ct = default);

    /// <summary>Open a read stream over an asset's bytes for downloading.</summary>
    Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default);
}
