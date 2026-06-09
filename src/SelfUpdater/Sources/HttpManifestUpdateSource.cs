using Serde.Json;

namespace SelfUpdater.Sources;

/// <summary>
/// Update source backed by a JSON manifest you publish yourself (see
/// <see cref="ReleaseManifest"/>). Integrity is via SHA-256 rather than signed
/// release feeds. Asset <c>url</c>s are resolved relative to the manifest URL, so
/// they may be absolute or relative.
/// </summary>
public sealed class HttpManifestUpdateSource : IUpdateSource
{
    private readonly Uri _manifestUrl;
    private readonly HttpClient _http;

    public HttpManifestUpdateSource(Uri manifestUrl, HttpClient? http = null)
    {
        _manifestUrl = manifestUrl;
        _http = http ?? new HttpClient();
    }

    public async Task<UpdateRelease?> GetLatestReleaseAsync(string rid, CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await _http.GetStringAsync(_manifestUrl, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        ReleaseManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleaseManifest>(json);
        }
        catch (Exception)
        {
            return null;
        }

        return ManifestMapping.ToRelease(manifest, rid,
            url => new Uri(_manifestUrl, url).ToString());
    }

    public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
        _http.GetStreamAsync(asset.Location, ct);
}
