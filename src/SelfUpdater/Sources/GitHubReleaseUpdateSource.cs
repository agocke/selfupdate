using System.Net.Http.Headers;
using Serde;
using Serde.Json;

namespace SelfUpdater.Sources;

/// <summary>
/// Update source backed by the GitHub Releases API. Discovers the newest release
/// from <c>tag_name</c> and picks the release asset whose file name matches the
/// requested RID.
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
    private readonly Func<CancellationToken, Task<string?>>? _authToken;
    private readonly Func<string, string, bool> _assetMatches;
    private readonly HttpClient _http;

    /// <param name="owner">Repository owner (user or org).</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="authToken">
    /// Optional callback returning a bearer token for the GitHub API. Required for
    /// private repositories; omit for public ones. Awaited once per request so a
    /// consumer can fetch/cache/refresh rotating, short-lived tokens (e.g. a vended
    /// GitHub App installation token) however it likes.
    /// </param>
    /// <param name="assetMatches">
    /// Predicate <c>(assetName, rid) =&gt; bool</c> selecting the asset for a RID.
    /// Defaults to a case-insensitive substring match on the RID.
    /// </param>
    /// <param name="http">Optional pre-configured <see cref="HttpClient"/> (e.g. with proxy/handlers).</param>
    public GitHubReleaseUpdateSource(
        string owner,
        string repo,
        Func<CancellationToken, Task<string?>>? authToken = null,
        Func<string, string, bool>? assetMatches = null,
        HttpClient? http = null)
    {
        _owner = owner;
        _repo = repo;
        _authToken = authToken;
        _assetMatches = assetMatches ?? DefaultAssetMatches;
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("selfupdater", "1.0"));
    }

    private static bool DefaultAssetMatches(string assetName, string rid) =>
        assetName.Contains(rid, StringComparison.OrdinalIgnoreCase);

    private bool IsPrivate => _authToken is not null;

    public async Task<UpdateRelease?> GetLatestReleaseAsync(string rid, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        await AddAuthAsync(req, ct).ConfigureAwait(false);

        string json;
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        GitHubRelease release;
        try
        {
            release = JsonSerializer.Deserialize<GitHubRelease>(json);
        }
        catch (Exception)
        {
            return null;
        }

        if (!SemVer.TryParse(release.TagName, out var version))
            return null;

        UpdateAsset? asset = null;
        foreach (var a in release.Assets)
        {
            if (_assetMatches(a.Name, rid))
            {
                // For private repos download through the authenticated asset API
                // endpoint; for public repos the browser URL needs no credentials.
                var location = IsPrivate ? (a.Url ?? a.BrowserDownloadUrl) : a.BrowserDownloadUrl;
                asset = new UpdateAsset(rid, location, Size: a.Size);
                break;
            }
        }

        return new UpdateRelease(version, asset);
    }

    public async Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, asset.Location);
        // The asset API returns a 302 to a signed CDN URL when asked for octets.
        // HttpClient drops the Authorization header on the cross-host redirect, so
        // sending it here is safe for both public and private downloads.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        await AddAuthAsync(req, ct).ConfigureAwait(false);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
public sealed partial record GitHubRelease
{
    [SerdeMemberOptions(Rename = "tag_name")]
    public required string TagName { get; init; }

    public required List<GitHubAsset> Assets { get; init; }
}

[GenerateSerde]
public sealed partial record GitHubAsset
{
    public required string Name { get; init; }

    /// <summary>The asset API URL, used for authenticated (private) downloads.</summary>
    public string? Url { get; init; }

    [SerdeMemberOptions(Rename = "browser_download_url")]
    public required string BrowserDownloadUrl { get; init; }

    public long? Size { get; init; }
}
