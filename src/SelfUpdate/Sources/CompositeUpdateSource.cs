using System.Runtime.CompilerServices;

namespace SelfUpdate.Sources;

/// <summary>
/// Combines several <see cref="IUpdateSource"/> backends into one. Every reachable
/// backend is queried; the newest version wins (ties prefer a backend that
/// actually has an asset for this RID, then earlier list order). Backends that
/// are unreachable or fail to parse are skipped, so listing a primary and a
/// mirror gives automatic fallback. Downloads are routed back to whichever
/// backend produced the winning asset.
/// </summary>
public sealed class CompositeUpdateSource : IUpdateSource
{
    private readonly IReadOnlyList<IUpdateSource> _sources;
    private readonly ConditionalWeakTable<UpdateAsset, IUpdateSource> _assetOwners = new();

    public CompositeUpdateSource(params IUpdateSource[] sources)
        : this((IReadOnlyList<IUpdateSource>)sources) { }

    public CompositeUpdateSource(IReadOnlyList<IUpdateSource> sources)
    {
        if (sources.Count == 0)
            throw new ArgumentException("At least one update source is required.", nameof(sources));
        _sources = sources;
    }

    public async Task<UpdateRelease?> GetLatestReleaseAsync(string rid, CancellationToken ct = default)
    {
        var releases = await Task.WhenAll(
            _sources.Select(s => SafeGetAsync(s, rid, ct))).ConfigureAwait(false);

        (IUpdateSource Source, UpdateRelease Release)? best = null;
        for (var i = 0; i < releases.Length; i++)
        {
            if (releases[i] is not { } release)
                continue;
            if (best is not { } b || IsBetter(release, b.Release))
                best = (_sources[i], release);
        }

        if (best is not { } winner)
            return null;

        if (winner.Release.Asset is { } asset)
            _assetOwners.AddOrUpdate(asset, winner.Source);

        return winner.Release;
    }

    public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default)
    {
        if (_assetOwners.TryGetValue(asset, out var owner))
            return owner.OpenAssetAsync(asset, ct);

        // Asset wasn't produced by a GetLatestReleaseAsync on this composite; fall
        // back to trying each backend in order.
        return OpenViaFallbackAsync(asset, ct);
    }

    private async Task<Stream> OpenViaFallbackAsync(UpdateAsset asset, CancellationToken ct)
    {
        foreach (var source in _sources)
        {
            try
            {
                return await source.OpenAssetAsync(asset, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Try the next backend.
            }
        }
        throw new InvalidOperationException("No backend could open the requested asset.");
    }

    private static bool IsBetter(UpdateRelease candidate, UpdateRelease current)
    {
        var cmp = candidate.Version.CompareTo(current.Version);
        if (cmp != 0)
            return cmp > 0;
        // Same version: prefer the one that actually has a downloadable asset.
        return candidate.Asset is not null && current.Asset is null;
    }

    private static async Task<UpdateRelease?> SafeGetAsync(IUpdateSource source, string rid, CancellationToken ct)
    {
        try
        {
            return await source.GetLatestReleaseAsync(rid, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }
}
