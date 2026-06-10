using Serde.Json;

namespace SelfUpdater.Sources;

/// <summary>
/// Update source backed by a local directory or network share containing a
/// <see cref="ReleaseManifest"/> (default file name <c>releases.json</c>) and the
/// asset files. Asset <c>url</c>s are treated as paths relative to the directory
/// (absolute paths are honored too). Useful for LAN/offline distribution, air-gapped
/// rollouts, and tests — no HTTP involved.
/// </summary>
public sealed class DirectoryUpdateSource : IUpdateSource
{
    private readonly string _directory;
    private readonly string _manifestFileName;

    public DirectoryUpdateSource(string directory, string manifestFileName = "releases.json")
    {
        _directory = directory;
        _manifestFileName = manifestFileName;
    }

    public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(_directory, _manifestFileName);
        if (!File.Exists(manifestPath))
            return [];

        ReleaseManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<ReleaseManifest>(json);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }

        return ManifestMapping.ToReleases(manifest, ResolvePath);
    }

    public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(asset.Location));

    private string ResolvePath(string url) =>
        Path.IsPathRooted(url) ? url : Path.Combine(_directory, url);
}
