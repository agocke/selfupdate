using System.Runtime.CompilerServices;

namespace SelfUpdater.Sources;

/// <summary>
/// Combines several <see cref="IUpdateSource"/> backends into one. Every reachable
/// backend is queried and their releases are merged into a single list, so the
/// caller sees the union of builds across all backends. Backends that are
/// unreachable or fail to parse are skipped, so listing a primary and a mirror
/// gives automatic fallback. Downloads are routed back to whichever backend
/// produced the chosen asset.
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

    public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(string rid, CancellationToken ct = default)
    {
        var perSource = await Task.WhenAll(
            _sources.Select(s => SafeGetAsync(s, rid, ct))).ConfigureAwait(false);

        var merged = new List<UpdateRelease>();
        for (var i = 0; i < perSource.Length; i++)
        {
            foreach (var release in perSource[i])
            {
                if (release.Asset is { } asset)
                    _assetOwners.AddOrUpdate(asset, _sources[i]);
                merged.Add(release);
            }
        }
        return merged;
    }

    public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default)
    {
        if (_assetOwners.TryGetValue(asset, out var owner))
            return owner.OpenAssetAsync(asset, ct);

        // Asset wasn't produced by a GetReleasesAsync on this composite; fall back
        // to trying each backend in order.
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

    private static async Task<IReadOnlyList<UpdateRelease>> SafeGetAsync(
        IUpdateSource source, string rid, CancellationToken ct)
    {
        try
        {
            return await source.GetReleasesAsync(rid, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }
}
