using Semver;

namespace SelfUpdater.Sources;

/// <summary>
/// Maps a source's asset name (a file name, a GitHub asset name, ...) to the
/// <c>(Version, Rid)</c> it represents, or <c>null</c> to skip it. Every source
/// runs candidate names through one of these: the returned <c>Rid</c> becomes the
/// asset's <see cref="Asset.Name"/> (which the consumer matches against its
/// platform) and the <c>Version</c> groups assets into releases. Returning
/// <c>null</c> filters the asset out, so a source surfaces only the builds whose
/// names follow the expected scheme.
/// </summary>
public delegate (SemVersion Version, string Rid)? AssetNameParser(string name);

/// <summary>
/// Shared name → release logic used by every <see cref="IUpdateSource"/>. The
/// default convention is <c>{appName}-{version}-{rid}</c> (e.g.
/// <c>myapp-1.2.3-osx-arm64</c>); a source's constructor accepts a custom
/// <see cref="AssetNameParser"/> for any other scheme.
/// </summary>
internal static class AssetNaming
{
    /// <summary>One named, downloadable artifact a source has discovered.</summary>
    /// <param name="Name">The source's asset name, fed to the <see cref="AssetNameParser"/>.</param>
    /// <param name="Location">Where the asset can be opened from (path or URL).</param>
    /// <param name="Sha256">Optional integrity hash, if the source published one.</param>
    /// <param name="Size">Optional asset size in bytes, if known.</param>
    /// <param name="PrereleaseHint">
    /// A source-side prerelease signal (e.g. GitHub's <c>prerelease</c> flag) OR-ed
    /// into the release's final <see cref="Release.IsPrerelease"/>, on top of whatever
    /// the parsed version itself indicates.
    /// </param>
    public readonly record struct Candidate(
        string Name,
        string Location,
        string? Sha256 = null,
        long? Size = null,
        bool PrereleaseHint = false
    );

    /// <summary>
    /// The default <c>{appName}-{version}-{rid}</c> parser: it takes everything up to
    /// the first <c>-</c> after the <c>{appName}-</c> prefix as the version (so it fits
    /// simple <c>MAJOR.MINOR.PATCH</c> versions), and the remainder as the rid. Names
    /// without the prefix, or whose version segment does not parse, are skipped.
    /// </summary>
    public static AssetNameParser DefaultParser(string appName)
    {
        var prefix = appName + "-";
        return name =>
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            var rest = name[prefix.Length..];
            var dash = rest.IndexOf('-');
            if (dash <= 0 || dash == rest.Length - 1)
                return null;

            return SemVersion.TryParse(rest[..dash], SemVersionStyles.Any, out var version)
                ? (version, rest[(dash + 1)..])
                : null;
        };
    }

    /// <summary>
    /// Parse each candidate's name, drop the ones the parser rejects, and group the
    /// rest into <see cref="Release"/>s by version. The candidate's <c>Rid</c> becomes
    /// the asset's <see cref="Asset.Name"/>.
    /// </summary>
    public static IReadOnlyList<Release> ToReleases(
        IEnumerable<Candidate> candidates,
        AssetNameParser parse
    )
    {
        var byVersion = new Dictionary<SemVersion, (List<Asset> Assets, bool Prerelease)>();
        foreach (var c in candidates)
        {
            if (parse(c.Name) is not { } parsed)
                continue;
            var (version, rid) = parsed;

            var asset = new Asset(rid, c.Location, c.Sha256, c.Size);
            if (byVersion.TryGetValue(version, out var entry))
                byVersion[version] = (entry.Assets, entry.Prerelease || c.PrereleaseHint);
            else
                byVersion[version] = entry = ([], c.PrereleaseHint);
            entry.Assets.Add(asset);
        }

        var releases = new List<Release>(byVersion.Count);
        foreach (var (version, entry) in byVersion)
            releases.Add(
                new Release(version, entry.Assets, version.IsPrerelease || entry.Prerelease)
            );
        return releases;
    }
}
