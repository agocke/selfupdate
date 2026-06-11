namespace SelfUpdater.Sources;

/// <summary>
/// Update source backed by a local directory or network share. Rather than reading
/// a manifest, it simply lists the directory and infers releases from the binary
/// file names. The default naming convention is <c>{appName}-{version}-{rid}</c>
/// (e.g. <c>myapp-1.2.3-osx-arm64</c>); supply a custom <see cref="AssetNameParser"/>
/// to the constructor for any other scheme. Files whose names the parser rejects are
/// ignored, so the directory may also hold checksums, notes, or unrelated files.
/// <para>
/// Integrity is optional and opt-in: if a sidecar file named
/// <c>{binary}.sha256</c> sits next to a binary, its contents are used as that
/// asset's <see cref="Asset.Sha256"/> (a bare hash, or the leading
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
        _parse = parse ?? AssetNaming.DefaultParser(appName);
    }

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory))
            return [];

        var candidates = new List<AssetNaming.Candidate>();
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(ChecksumExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            var sha256 = await ReadSidecarChecksumAsync(path, ct).ConfigureAwait(false);
            candidates.Add(new(fileName, path, sha256, new FileInfo(path).Length));
        }

        return AssetNaming.ToReleases(candidates, _parse);
    }

    public Task<Stream> OpenAssetAsync(Asset asset, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(asset.Location));

    private static async Task<string?> ReadSidecarChecksumAsync(
        string assetPath,
        CancellationToken ct
    )
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
}
