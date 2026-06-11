using Semver;

namespace SelfUpdater.Sources;

/// <summary>
/// Splits a release binary's file name into its version and platform identifier.
/// The returned <c>Rid</c> becomes the asset's <see cref="UpdateAsset.Name"/>, which
/// the consumer matches against its platform. Return <c>null</c> for files that are
/// not release binaries (they are ignored).
/// </summary>
public delegate (SemVersion Version, string Rid)? AssetNameParser(string fileName);

/// <summary>
/// Update source backed by a local directory or network share. Rather than reading
/// a manifest, it simply lists the directory and infers releases from the binary
/// file names. The default naming convention is <c>{appName}-{version}-{rid}</c>
/// (e.g. <c>myapp-1.2.3-osx-arm64</c>); supply a custom <see cref="AssetNameParser"/>
/// to the constructor for any other scheme.
/// <para>
/// Integrity is optional and opt-in: if a sidecar file named
/// <c>{binary}.sha256</c> sits next to a binary, its contents are used as that
/// asset's <see cref="UpdateAsset.Sha256"/> (a bare hash, or the leading
/// <c>sha256sum</c>-style <c>&lt;hash&gt;&#160;&#160;filename</c> token).
/// </para>
/// Useful for LAN/offline distribution, air-gapped rollouts, and tests — no HTTP
/// involved.
/// </summary>
public sealed class DirectoryUpdateSource : IUpdateSource
{
    private const string ChecksumExtension = ".sha256";

    private readonly string _directory;
    private readonly AssetNameParser _parse;

    /// <param name="directory">Directory or share holding the release binaries.</param>
    /// <param name="appName">
    /// Application name used by the default <c>{appName}-{version}-{rid}</c> naming
    /// convention. The default parser takes everything up to the first <c>-</c> after
    /// this prefix as the version, so it fits simple <c>MAJOR.MINOR.PATCH</c> versions;
    /// pass <paramref name="parse"/> for prerelease tags or any other scheme.
    /// </param>
    /// <param name="parse">
    /// Optional override for how file names map to (version, rid). When omitted the
    /// default <c>{appName}-{version}-{rid}</c> convention is used.
    /// </param>
    public DirectoryUpdateSource(string directory, string appName, AssetNameParser? parse = null)
    {
        _directory = directory;
        _parse = parse ?? DefaultParser(appName);
    }

    public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory))
            return [];

        var byVersion = new Dictionary<SemVersion, List<UpdateAsset>>();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(ChecksumExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_parse(fileName) is not { } parsed)
                continue;

            var (version, rid) = parsed;
            var sha256 = await ReadSidecarChecksumAsync(path, ct).ConfigureAwait(false);
            var asset = new UpdateAsset(rid, path, sha256, new FileInfo(path).Length);

            if (!byVersion.TryGetValue(version, out var assets))
                byVersion[version] = assets = [];
            assets.Add(asset);
        }

        var releases = new List<UpdateRelease>(byVersion.Count);
        foreach (var (version, assets) in byVersion)
            releases.Add(new UpdateRelease(version, assets, version.IsPrerelease));
        return releases;
    }

    public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(asset.Location));

    private static async Task<string?> ReadSidecarChecksumAsync(string assetPath, CancellationToken ct)
    {
        var sidecar = assetPath + ChecksumExtension;
        if (!File.Exists(sidecar))
            return null;

        var text = (await File.ReadAllTextAsync(sidecar, ct).ConfigureAwait(false)).Trim();
        if (text.Length == 0)
            return null;

        // Accept a bare hash or the "<hash>  filename" sha256sum format.
        var space = text.IndexOfAny([' ', '\t']);
        return space > 0 ? text[..space] : text;
    }

    private static AssetNameParser DefaultParser(string appName)
    {
        var prefix = appName + "-";
        return fileName =>
        {
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            var rest = fileName[prefix.Length..];
            var dash = rest.IndexOf('-');
            if (dash <= 0 || dash == rest.Length - 1)
                return null;

            return SemVersion.TryParse(rest[..dash], SemVersionStyles.Any, out var version)
                ? (version, rest[(dash + 1)..])
                : null;
        };
    }
}
