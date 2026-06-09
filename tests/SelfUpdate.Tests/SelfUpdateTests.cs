using System.Net;
using System.Text;

using SelfUpdate;
using SelfUpdate.Sources;

namespace SelfUpdate.Tests;

public class SelfUpdateTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v0.1.0", 0, 1, 0, null)]
    [InlineData("2.0", 2, 0, 0, null)]
    [InlineData("1.2.3-beta.1+build7", 1, 2, 3, "beta.1")]
    public void SemVer_Parses(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(new SemVer(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("")]
    public void SemVer_RejectsInvalid(string text) =>
        Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void SemVer_Orders()
    {
        Assert.True(SemVer.Parse("1.0.0") < SemVer.Parse("1.0.1"));
        Assert.True(SemVer.Parse("1.2.0") > SemVer.Parse("1.1.9"));
        // Prerelease sorts below its release.
        Assert.True(SemVer.Parse("1.0.0-beta") < SemVer.Parse("1.0.0"));
        Assert.True(SemVer.Parse("1.0.0-alpha") < SemVer.Parse("1.0.0-beta"));
        Assert.True(SemVer.Parse("1.0.0-alpha.1") < SemVer.Parse("1.0.0-alpha.2"));
        // Build metadata is ignored.
        Assert.Equal(SemVer.Parse("1.0.0+a"), SemVer.Parse("1.0.0+b"));
    }

    [Fact]
    public async Task HttpManifest_ResolvesAssetForRid()
    {
        const string json = """
        {
          "latestVersion": {
            "version": "0.2.0",
            "assets": {
              "osx-arm64": { "url": "https://example/app-osx-arm64", "sha256": "abc" },
              "linux-x64": { "url": "https://example/app-linux-x64" }
            }
          }
        }
        """;
        var source = new HttpManifestUpdateSource(
            new Uri("https://example/releases.json"), StubClient(json));

        var release = await source.GetLatestReleaseAsync("osx-arm64");

        Assert.NotNull(release);
        Assert.Equal(SemVer.Parse("0.2.0"), release!.Version);
        Assert.NotNull(release.Asset);
        Assert.Equal("https://example/app-osx-arm64", release.Asset!.Location);
        Assert.Equal("abc", release.Asset.Sha256);
    }

    [Fact]
    public async Task HttpManifest_ResolvesRelativeAssetUrl()
    {
        const string json = """
        { "latestVersion": { "version": "0.2.0", "assets": { "osx-arm64": { "url": "bin/app-osx-arm64" } } } }
        """;
        var source = new HttpManifestUpdateSource(
            new Uri("https://example/downloads/releases.json"), StubClient(json));

        var release = await source.GetLatestReleaseAsync("osx-arm64");

        Assert.Equal("https://example/downloads/bin/app-osx-arm64", release!.Asset!.Location);
    }

    [Fact]
    public async Task HttpManifest_NoAssetForUnknownRid()
    {
        const string json = """
        { "latestVersion": { "version": "0.2.0", "assets": { "win-x64": { "url": "u" } } } }
        """;
        var source = new HttpManifestUpdateSource(
            new Uri("https://example/releases.json"), StubClient(json));

        var release = await source.GetLatestReleaseAsync("osx-arm64");

        Assert.NotNull(release);
        Assert.Null(release!.Asset);
    }

    [Fact]
    public async Task GitHubSource_ParsesSnakeCaseAndMatchesRid()
    {
        const string json = """
        {
          "tag_name": "v0.3.0",
          "html_url": "https://github.com/agocke/app/releases/tag/v0.3.0",
          "assets": [
            { "name": "app-osx-arm64", "browser_download_url": "https://dl/osx", "size": 42 },
            { "name": "app-linux-x64", "browser_download_url": "https://dl/linux" }
          ]
        }
        """;
        var source = new GitHubReleaseUpdateSource("agocke", "app", http: StubClient(json));

        var release = await source.GetLatestReleaseAsync("osx-arm64");

        Assert.NotNull(release);
        Assert.Equal(SemVer.Parse("0.3.0"), release!.Version);
        Assert.Equal("https://dl/osx", release.Asset!.Location);
        Assert.Equal(42, release.Asset.Size);
    }

    [Fact]
    public async Task DirectorySource_ReadsManifestAndOpensAsset()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdate-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "app-osx-arm64"), "the-binary-bytes");
            File.WriteAllText(Path.Combine(dir, "releases.json"), """
            { "latestVersion": { "version": "1.5.0", "assets": { "osx-arm64": { "url": "app-osx-arm64" } } } }
            """);
            var source = new DirectoryUpdateSource(dir);

            var release = await source.GetLatestReleaseAsync("osx-arm64");

            Assert.NotNull(release);
            Assert.Equal(SemVer.Parse("1.5.0"), release!.Version);
            Assert.Equal(Path.Combine(dir, "app-osx-arm64"), release.Asset!.Location);

            await using var stream = await source.OpenAssetAsync(release.Asset);
            using var reader = new StreamReader(stream);
            Assert.Equal("the-binary-bytes", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_MissingManifestReturnsNull()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdate-test-").FullName;
        try
        {
            var source = new DirectoryUpdateSource(dir);
            Assert.Null(await source.GetLatestReleaseAsync("osx-arm64"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Composite_PicksNewestVersionAndRoutesDownload()
    {
        var older = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.0.0"), new UpdateAsset("osx-arm64", "from-older")), "older-bytes");
        var newer = new FakeSource(new UpdateRelease(
            SemVer.Parse("2.0.0"), new UpdateAsset("osx-arm64", "from-newer")), "newer-bytes");

        var composite = new CompositeUpdateSource(older, newer);

        var release = await composite.GetLatestReleaseAsync("osx-arm64");
        Assert.Equal(SemVer.Parse("2.0.0"), release!.Version);

        // The download must be routed back to the backend that produced the asset.
        await using var stream = await composite.OpenAssetAsync(release.Asset!);
        using var reader = new StreamReader(stream);
        Assert.Equal("newer-bytes", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Composite_SkipsUnreachableBackends()
    {
        var broken = new ThrowingSource();
        var working = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.2.3"), new UpdateAsset("osx-arm64", "loc")), "bytes");

        var composite = new CompositeUpdateSource(broken, working);

        var release = await composite.GetLatestReleaseAsync("osx-arm64");
        Assert.Equal(SemVer.Parse("1.2.3"), release!.Version);
    }

    [Fact]
    public async Task Composite_PrefersBackendWithAssetOnVersionTie()
    {
        var noAsset = new FakeSource(new UpdateRelease(SemVer.Parse("2.0.0"), Asset: null), "");
        var withAsset = new FakeSource(new UpdateRelease(
            SemVer.Parse("2.0.0"), new UpdateAsset("osx-arm64", "loc")), "bytes");

        var composite = new CompositeUpdateSource(noAsset, withAsset);

        var release = await composite.GetLatestReleaseAsync("osx-arm64");
        Assert.NotNull(release!.Asset);
    }

    [Fact]
    public async Task Composite_AllBackendsDownReturnsNull()
    {
        var composite = new CompositeUpdateSource(new ThrowingSource(), new ThrowingSource());
        Assert.Null(await composite.GetLatestReleaseAsync("osx-arm64"));
    }

    [Fact]
    public async Task Updater_DetectsAvailableAndUpToDate()
    {
        var newer = new FakeSource(new UpdateRelease(
            SemVer.Parse("9.9.9"), new UpdateAsset("osx-arm64", "https://dl")));
        var available = await new Updater(newer, Options("1.0.0")).CheckAsync();
        Assert.True(available.UpdateAvailable);
        Assert.Equal(SemVer.Parse("9.9.9"), available.Latest);

        var same = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.0.0"), new UpdateAsset("osx-arm64", "https://dl")));
        var upToDate = await new Updater(same, Options("1.0.0")).CheckAsync();
        Assert.False(upToDate.UpdateAvailable);
    }

    [Fact]
    public async Task Updater_ReportsNoAssetForPlatform()
    {
        var noAsset = new FakeSource(new UpdateRelease(SemVer.Parse("2.0.0"), Asset: null));
        var result = await new Updater(noAsset, Options("1.0.0", allowNonSingleFile: true)).UpdateAsync();
        Assert.Equal(UpdateOutcome.NoAssetForPlatform, result.Outcome);
    }

    [Fact]
    public async Task GitHubSource_PrivateRepoUsesAuthAndAssetApiUrl()
    {
        const string json = """
        {
          "tag_name": "v0.3.0",
          "assets": [
            {
              "name": "app-osx-arm64",
              "url": "https://api.github.com/repos/agocke/app/releases/assets/99",
              "browser_download_url": "https://dl/osx",
              "size": 42
            }
          ]
        }
        """;
        string? seenAuth = null;
        string? seenAccept = null;
        var handler = new CapturingHandler(json, req =>
        {
            seenAuth = req.Headers.Authorization?.ToString();
            seenAccept = req.Headers.Accept.ToString();
        });
        var source = new GitHubReleaseUpdateSource(
            "agocke", "app", authToken: _ => ValueTask.FromResult<string?>("tok-123"), http: new HttpClient(handler));

        var release = await source.GetLatestReleaseAsync("osx-arm64");

        // Private repos download via the asset API url, not browser_download_url.
        Assert.Equal("https://api.github.com/repos/agocke/app/releases/assets/99", release!.Asset!.Location);
        Assert.Equal("Bearer tok-123", seenAuth);

        await using var _ = await source.OpenAssetAsync(release.Asset);
        Assert.Contains("application/octet-stream", seenAccept);
        Assert.Equal("Bearer tok-123", seenAuth);
    }

    private static UpdaterOptions Options(string current, bool allowNonSingleFile = false) => new()
    {
        CurrentVersion = SemVer.Parse(current),
        Rid = "osx-arm64",
        Relaunch = false,
        AllowNonSingleFile = allowNonSingleFile,
        Log = TextWriter.Null,
    };

    private static HttpClient StubClient(string body) =>
        new(new StubHandler(body));

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class CapturingHandler(string body, Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeSource(UpdateRelease? release, string assetBody = "") : IUpdateSource
    {
        public Task<UpdateRelease?> GetLatestReleaseAsync(string rid, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(assetBody)));
    }

    private sealed class ThrowingSource : IUpdateSource
    {
        public Task<UpdateRelease?> GetLatestReleaseAsync(string rid, CancellationToken ct = default) =>
            throw new HttpRequestException("backend down");

        public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
            throw new HttpRequestException("backend down");
    }
}
