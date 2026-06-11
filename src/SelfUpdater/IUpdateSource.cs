using Semver;

namespace SelfUpdater;

/// <summary>
/// A downloadable build artifact identified by an opaque <see cref="Name"/>.
/// The name is the publisher's contract — a GitHub asset file name or a source's
/// platform identifier — and is never interpreted by the engine; the consumer selects
/// which asset matches its platform (e.g. by runtime identifier). <see cref="Location"/>
/// is likewise opaque to the engine and interpreted only by the <see cref="IUpdateSource"/>
/// that produced it (a URL for the GitHub source, a file path for the directory
/// source, but it could equally be a blob id or torrent magnet for some future source).
/// </summary>
public sealed record Asset(string Name, string Location, string? Sha256 = null, long? Size = null);

/// <summary>A release a source knows about, with every asset it ships.</summary>
/// <param name="Version">The release version, parsed from the source's tag or file name.</param>
/// <param name="Assets">All artifacts published for this release; the consumer picks the one for its platform.</param>
/// <param name="IsPrerelease">
/// True when the publisher marked this a prerelease (GitHub's <c>prerelease</c>
/// flag, or a SemVer prerelease suffix). The engine never filters on this; it is
/// surfaced so the consumer can choose to skip prereleases.
/// </param>
public sealed record Release(
    SemVersion Version,
    IReadOnlyList<Asset> Assets,
    bool IsPrerelease = false
);

/// <summary>
/// Pluggable strategy for discovering and fetching new builds. This is the seam
/// that keeps the updater independent of any particular distribution mechanism:
/// the engine only ever asks "what releases exist?" and "give me a stream of this
/// asset's bytes". Concrete sources decide *where* builds live and *how* they are
/// described (a GitHub release, a directory/share, a package feed, ...).
/// Sources never compare versions, decide what is "new", or pick which asset suits
/// the running platform — all of that is consumer policy.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// List the releases this source knows about, each with all of its assets.
    /// Returns an empty list if the source could not be reached or parsed. Order is
    /// not significant; the caller decides which (if any) releases are newer than
    /// what it is running and which asset matches its platform. Sources that only
    /// track a single "latest" build return a one-element list.
    /// </summary>
    Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default);

    /// <summary>Open a read stream over an asset's bytes for downloading.</summary>
    Task<Stream> OpenAssetAsync(Asset asset, CancellationToken ct = default);
}
