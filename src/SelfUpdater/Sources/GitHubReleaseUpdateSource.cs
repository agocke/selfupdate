using System.Net.Http.Headers;
using Serde;
using Serde.Json;

namespace SelfUpdater.Sources;

/// <summary>
/// Update source backed by the GitHub Releases API. Lists the repo's releases and
/// infers releases from the asset file names, using the default
/// <c>{appName}-{version}-{rid}</c> naming convention (or a custom
/// <see cref="AssetNameParser"/>); assets that don't follow the convention are
/// ignored. The caller decides which release is newer than what it is running and
/// which asset matches its platform.
/// <para>
/// Draft releases are skipped (they are unpublished); prereleases are included and
/// flagged via <see cref="Release.IsPrerelease"/> so the caller can filter
/// them. Only the first page of releases (the most recent ~30) is returned, which
/// is sufficient for "is there anything newer than me" decisions.
/// </para>
/// <para>
/// When GitHub reports an asset <c>digest</c> (e.g. <c>sha256:…</c>) it is surfaced
/// as <see cref="Asset.Sha256"/>, so the engine verifies integrity of the
/// download against GitHub's published hash. (This is integrity, not authenticity:
/// it does not prove the publisher's identity — use signed releases/manifests for
/// that.)
/// </para>
/// <para>
/// Works with <b>private</b> repositories: supply <c>authToken</c> and the source
/// authenticates the API call and downloads the asset through the authenticated
/// asset API endpoint (with <c>Accept: application/octet-stream</c>) rather than
/// the public <c>browser_download_url</c>. The token is fetched per request via a
/// delegate so short-lived / rotating credentials (GitHub App installation tokens,
/// fine-grained PATs) refresh automatically.
/// </para>
/// </summary>
public sealed class GitHubReleaseUpdateSource : IUpdateSource
{
    private const string ApiVersion = "2022-11-28";

    private readonly string _owner;
    private readonly string _repo;
    private readonly AssetNameParser _parse;
    private readonly Func<CancellationToken, Task<string?>>? _authToken;
    private readonly HttpClient _http;

    /// <param name="owner">Repository owner (user or org).</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="appName">
    /// Application name for the default <c>{appName}-{version}-{rid}</c> asset-naming
    /// convention. Each release asset's file name is parsed into a (version, rid);
    /// assets that don't follow the convention are ignored. Pass <paramref name="parse"/>
    /// for any other scheme.
    /// </param>
    /// <param name="parse">
    /// Optional override for how asset names map to (version, rid). When omitted the
    /// default <c>{appName}-{version}-{rid}</c> convention is used.
    /// </param>
    /// <param name="authToken">
    /// Optional callback returning a bearer token for the GitHub API. Required for
    /// private repositories; omit for public ones. Awaited once per request so a
    /// consumer can fetch/cache/refresh rotating, short-lived tokens (e.g. a vended
    /// GitHub App installation token) however it likes.
    /// </param>
    /// <param name="http">Optional pre-configured <see cref="HttpClient"/> (e.g. with proxy/handlers).</param>
    public GitHubReleaseUpdateSource(
        string owner,
        string repo,
        string appName,
        AssetNameParser? parse = null,
        Func<CancellationToken, Task<string?>>? authToken = null,
        HttpClient? http = null
    )
    {
        _owner = owner;
        _repo = repo;
        _parse = parse ?? AssetNaming.DefaultParser(appName);
        _authToken = authToken;
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("selfupdater", "1.0")
            );
    }

    private bool IsPrivate => _authToken is not null;

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        await AddAuthAsync(req, ct).ConfigureAwait(false);

        string json;
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return [];
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }

        List<GitHubRelease> releases;
        try
        {
            releases = JsonSerializer.Deserialize(json, List<GitHubRelease>.Deserialize);
        }
        catch (Exception)
        {
            return [];
        }

        var candidates = new List<AssetNaming.Candidate>();
        foreach (var release in releases)
        {
            if (release.Draft == true)
                continue;
            foreach (var a in release.Assets)
            {
                // For private repos download through the authenticated asset API
                // endpoint; for public repos the browser URL needs no credentials.
                var location = IsPrivate ? (a.Url ?? a.BrowserDownloadUrl) : a.BrowserDownloadUrl;
                candidates.Add(
                    new(
                        a.Name,
                        location,
                        ParseDigest(a.Digest),
                        a.Size,
                        release.Prerelease ?? false
                    )
                );
            }
        }

        return AssetNaming.ToReleases(candidates, _parse);
    }

    /// <summary>GitHub reports asset digests as "&lt;algo&gt;:&lt;hex&gt;"; we use the SHA-256 hex.</summary>
    private static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrEmpty(digest))
            return null;
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : null;
    }

    public async Task<Stream> OpenAssetAsync(Asset asset, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, asset.Location);
        // The asset API returns a 302 to a signed CDN URL when asked for octets.
        // HttpClient drops the Authorization header on the cross-host redirect, so
        // sending it here is safe for both public and private downloads.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        await AddAuthAsync(req, ct).ConfigureAwait(false);

        var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    private async Task AddAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_authToken is null)
            return;
        if (await _authToken(ct).ConfigureAwait(false) is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

[GenerateSerde]
internal sealed partial record GitHubRelease
{
    [SerdeMemberOptions(Rename = "tag_name")]
    public required string TagName { get; init; }

    public bool? Draft { get; init; }

    public bool? Prerelease { get; init; }

    public required List<GitHubAsset> Assets { get; init; }
}

[GenerateSerde]
internal sealed partial record GitHubAsset
{
    public required string Name { get; init; }

    /// <summary>The asset API URL, used for authenticated (private) downloads.</summary>
    public string? Url { get; init; }

    [SerdeMemberOptions(Rename = "browser_download_url")]
    public required string BrowserDownloadUrl { get; init; }

    /// <summary>GitHub-published content digest, e.g. <c>sha256:…</c> (may be absent).</summary>
    public string? Digest { get; init; }

    public long? Size { get; init; }
}
