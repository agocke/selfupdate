namespace SelfUpdater;

/// <summary>
/// A downloadable build artifact for a specific runtime identifier.
/// <see cref="Location"/> is opaque to the engine and interpreted only by the
/// <see cref="IUpdateSource"/> that produced it (a URL for the HTTP/GitHub
/// sources, but it could equally be a file path, blob id, or torrent magnet for
/// some future source).
/// </summary>
public sealed record UpdateAsset(string Rid, string Location, string? Sha256 = null, long? Size = null);

/// <summary>A release a source knows about, plus the asset for the requested RID (if any).</summary>
public sealed record UpdateRelease(SemVer Version, UpdateAsset? Asset);

/// <summary>
/// Pluggable strategy for discovering and fetching new builds. This is the seam
/// that keeps the updater independent of any particular distribution mechanism:
/// the engine only ever asks "what builds exist for my RID?" and "give me a
/// stream of this asset's bytes". Concrete sources decide *where* builds live and
/// *how* they are described (a JSON manifest, a GitHub release, a package feed, a
/// LAN share, ...). Sources never compare versions or decide what is "new" — that
/// policy belongs to the caller, which knows its own current version.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// List the releases this source knows about, each with the asset matching
    /// <paramref name="rid"/> (if any). Returns an empty list if the source could
    /// not be reached or parsed. A release's <see cref="UpdateRelease.Asset"/> may
    /// be <c>null</c> when that version ships nothing for this platform. Order is
    /// not significant; the caller decides which (if any) releases are newer than
    /// what it is running. Sources that only track a single "latest" build return
    /// a one-element list.
    /// </summary>
    Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(string rid, CancellationToken ct = default);

    /// <summary>Open a read stream over an asset's bytes for downloading.</summary>
    Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default);
}
