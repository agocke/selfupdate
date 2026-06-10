using System.Net;
using System.Text;

using SelfUpdater;
using SelfUpdater.Sources;

namespace SelfUpdater.Tests;

public class SelfUpdaterTests
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
    public void SemVer_IsPrerelease()
    {
        Assert.True(SemVer.Parse("1.0.0-rc.1").IsPrerelease);
        Assert.False(SemVer.Parse("1.0.0").IsPrerelease);
    }

    [Fact]
    public async Task HttpManifest_ExposesAllAssetsByName()
    {
        const string json = """
        {
          "releases": [
            {
              "version": "0.2.0",
              "assets": {
                "osx-arm64": { "url": "https://example/app-osx-arm64", "sha256": "abc" },
                "linux-x64": { "url": "https://example/app-linux-x64" }
              }
            }
          ]
        }
        """;
        var source = new HttpManifestUpdateSource(
            new Uri("https://example/releases.json"), StubClient(json));

        var release = Assert.Single(await source.GetReleasesAsync());

        Assert.Equal(SemVer.Parse("0.2.0"), release.Version);
        Assert.Equal(2, release.Assets.Count);
        var osx = Asset(release, "osx-arm64");
        Assert.Equal("https://example/app-osx-arm64", osx.Location);
        Assert.Equal("abc", osx.Sha256);
        Assert.Equal("https://example/app-linux-x64", Asset(release, "linux-x64").Location);
    }

    [Fact]
    public async Task HttpManifest_ResolvesRelativeAssetUrl()
    {
        const string json = """
        { "releases": [ { "version": "0.2.0", "assets": { "osx-arm64": { "url": "bin/app-osx-arm64" } } } ] }
        """;
        var source = new HttpManifestUpdateSource(
            new Uri("https://example/downloads/releases.json"), StubClient(json));

        var release = Assert.Single(await source.GetReleasesAsync());

        Assert.Equal("https://example/downloads/bin/app-osx-arm64", Asset(release, "osx-arm64").Location);
    }

    [Fact]
    public async Task HttpManifest_ListsMultipleReleases()
    {
        const string json = """
        {
          "releases": [
            { "version": "0.3.0", "assets": { "osx-arm64": { "url": "u3" } } },
            { "version": "0.2.0", "prerelease": true, "assets": { "osx-arm64": { "url": "u2" } } }
          ]
        }
        """;
        var source = new HttpManifestUpdateSource(
            new Uri("https://example/releases.json"), StubClient(json));

        var releases = await source.GetReleasesAsync();

        Assert.Equal(2, releases.Count);
        var pre = releases.Single(r => r.Version == SemVer.Parse("0.2.0"));
        Assert.True(pre.IsPrerelease);
        Assert.False(releases.Single(r => r.Version == SemVer.Parse("0.3.0")).IsPrerelease);
    }

    [Fact]
    public async Task GitHubSource_ListsReleasesWithAllAssets()
    {
        const string json = """
        [
          {
            "tag_name": "v0.3.0",
            "html_url": "https://github.com/agocke/app/releases/tag/v0.3.0",
            "assets": [
              { "name": "app-osx-arm64", "browser_download_url": "https://dl/osx-030", "size": 42,
                "digest": "sha256:DEADBEEF" },
              { "name": "app-linux-x64", "browser_download_url": "https://dl/linux-030" }
            ]
          },
          {
            "tag_name": "v0.2.0",
            "assets": [
              { "name": "app-osx-arm64", "browser_download_url": "https://dl/osx-020" }
            ]
          }
        ]
        """;
        var source = new GitHubReleaseUpdateSource("agocke", "app", http: StubClient(json));

        var releases = await source.GetReleasesAsync();

        Assert.Equal(2, releases.Count);
        Assert.Equal(SemVer.Parse("0.3.0"), releases[0].Version);
        Assert.Equal(2, releases[0].Assets.Count);
        var osx = Asset(releases[0], "app-osx-arm64");
        Assert.Equal("https://dl/osx-030", osx.Location);
        Assert.Equal(42, osx.Size);
        // GitHub's published digest becomes the integrity hash.
        Assert.Equal("DEADBEEF", osx.Sha256);
        Assert.Null(Asset(releases[0], "app-linux-x64").Sha256);
    }

    [Fact]
    public async Task GitHubSource_SkipsDraftsAndFlagsPrereleases()
    {
        const string json = """
        [
          { "tag_name": "v0.4.0", "draft": true,
            "assets": [ { "name": "a", "browser_download_url": "https://dl/draft" } ] },
          { "tag_name": "v0.3.0", "prerelease": true,
            "assets": [ { "name": "a", "browser_download_url": "https://dl/pre" } ] },
          { "tag_name": "v0.2.0",
            "assets": [ { "name": "a", "browser_download_url": "https://dl/rel" } ] }
        ]
        """;
        var source = new GitHubReleaseUpdateSource("agocke", "app", http: StubClient(json));

        var releases = await source.GetReleasesAsync();

        // The draft is gone; the prerelease is present and flagged.
        Assert.Equal(2, releases.Count);
        Assert.DoesNotContain(releases, r => r.Version == SemVer.Parse("0.4.0"));
        Assert.True(releases.Single(r => r.Version == SemVer.Parse("0.3.0")).IsPrerelease);
        Assert.False(releases.Single(r => r.Version == SemVer.Parse("0.2.0")).IsPrerelease);
    }

    [Fact]
    public async Task DirectorySource_ReadsManifestAndOpensAsset()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdater-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "app-osx-arm64"), "the-binary-bytes");
            File.WriteAllText(Path.Combine(dir, "releases.json"), """
            { "releases": [ { "version": "1.5.0", "assets": { "osx-arm64": { "url": "app-osx-arm64" } } } ] }
            """);
            var source = new DirectoryUpdateSource(dir);

            var release = Assert.Single(await source.GetReleasesAsync());

            Assert.Equal(SemVer.Parse("1.5.0"), release.Version);
            var asset = Asset(release, "osx-arm64");
            Assert.Equal(Path.Combine(dir, "app-osx-arm64"), asset.Location);

            await using var stream = await source.OpenAssetAsync(asset);
            using var reader = new StreamReader(stream);
            Assert.Equal("the-binary-bytes", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_MissingManifestReturnsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdater-test-").FullName;
        try
        {
            var source = new DirectoryUpdateSource(dir);
            Assert.Empty(await source.GetReleasesAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Composite_MergesReleasesAndRoutesDownload()
    {
        var older = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.0.0"), [new UpdateAsset("app", "from-older")]), "older-bytes");
        var newer = new FakeSource(new UpdateRelease(
            SemVer.Parse("2.0.0"), [new UpdateAsset("app", "from-newer")]), "newer-bytes");

        var composite = new CompositeUpdateSource(older, newer);

        var releases = await composite.GetReleasesAsync();
        Assert.Equal(2, releases.Count);

        // The download must be routed back to the backend that produced the asset.
        var chosen = releases.Single(r => r.Version == SemVer.Parse("2.0.0"));
        await using var stream = await composite.OpenAssetAsync(chosen.Assets[0]);
        using var reader = new StreamReader(stream);
        Assert.Equal("newer-bytes", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Composite_SkipsUnreachableBackends()
    {
        var broken = new ThrowingSource();
        var working = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.2.3"), [new UpdateAsset("app", "loc")]), "bytes");

        var composite = new CompositeUpdateSource(broken, working);

        var release = Assert.Single(await composite.GetReleasesAsync());
        Assert.Equal(SemVer.Parse("1.2.3"), release.Version);
    }

    [Fact]
    public async Task Composite_AllBackendsDownReturnsEmpty()
    {
        var composite = new CompositeUpdateSource(new ThrowingSource(), new ThrowingSource());
        Assert.Empty(await composite.GetReleasesAsync());
    }

    [Fact]
    public async Task Updater_DetectsAvailableAndUpToDate()
    {
        var newer = new FakeSource(new UpdateRelease(
            SemVer.Parse("9.9.9"), [new UpdateAsset("app", "https://dl")]));
        var available = await new Updater(newer, Options()).CheckAsync(SemVer.Parse("1.0.0"));
        Assert.True(available.UpdateAvailable);
        Assert.Equal(SemVer.Parse("9.9.9"), available.Latest!.Version);

        var same = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.0.0"), [new UpdateAsset("app", "https://dl")]));
        var upToDate = await new Updater(same, Options()).CheckAsync(SemVer.Parse("1.0.0"));
        Assert.False(upToDate.UpdateAvailable);
    }

    [Fact]
    public async Task Updater_PicksNewestAcrossMultipleReleases()
    {
        var source = new FakeSource(
            new UpdateRelease(SemVer.Parse("1.0.0"), [new UpdateAsset("app", "v1")]),
            new UpdateRelease(SemVer.Parse("3.0.0"), [new UpdateAsset("app", "v3")]),
            new UpdateRelease(SemVer.Parse("2.0.0"), [new UpdateAsset("app", "v2")]));

        var check = await new Updater(source, Options()).CheckAsync(SemVer.Parse("1.5.0"));

        Assert.True(check.UpdateAvailable);
        Assert.Equal(SemVer.Parse("3.0.0"), check.Latest!.Version);
    }

    [Fact]
    public async Task Updater_ReleaseFilterExcludesPrereleases()
    {
        var source = new FakeSource(
            new UpdateRelease(SemVer.Parse("2.0.0"), [new UpdateAsset("app", "stable")]),
            new UpdateRelease(SemVer.Parse("3.0.0-rc.1"), [new UpdateAsset("app", "rc")], IsPrerelease: true));

        // Without a filter the prerelease is newest; with one it is skipped.
        var all = await new Updater(source, Options()).CheckAsync(SemVer.Parse("1.0.0"));
        Assert.Equal(SemVer.Parse("3.0.0-rc.1"), all.Latest!.Version);

        var stableOnly = await new Updater(source, Options())
            .CheckAsync(SemVer.Parse("1.0.0"), r => !r.IsPrerelease);
        Assert.Equal(SemVer.Parse("2.0.0"), stableOnly.Latest!.Version);
    }

    [Fact]
    public async Task Updater_ReportsNoAssetForPlatform()
    {
        // A newer release exists but ships nothing the selector accepts.
        var source = new FakeSource(new UpdateRelease(
            SemVer.Parse("2.0.0"), [new UpdateAsset("win-x64", "loc")]));
        var result = await new Updater(source, Options(allowNonSingleFile: true))
            .UpdateAsync(SemVer.Parse("1.0.0"), a => a.Name == "osx-arm64");
        Assert.Equal(UpdateOutcome.NoAssetForPlatform, result.Outcome);
    }

    [Fact]
    public async Task Updater_UpToDateWhenNoNewerRelease()
    {
        var source = new FakeSource(new UpdateRelease(
            SemVer.Parse("1.0.0"), [new UpdateAsset("osx-arm64", "loc")]));
        var result = await new Updater(source, Options(allowNonSingleFile: true))
            .UpdateAsync(SemVer.Parse("1.0.0"), a => a.Name == "osx-arm64");
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task GitHubSource_PrivateRepoUsesAuthAndAssetApiUrl()
    {
        const string json = """
        [
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
        ]
        """;
        string? seenAuth = null;
        string? seenAccept = null;
        var handler = new CapturingHandler(json, req =>
        {
            seenAuth = req.Headers.Authorization?.ToString();
            seenAccept = req.Headers.Accept.ToString();
        });
        var source = new GitHubReleaseUpdateSource(
            "agocke", "app", authToken: _ => Task.FromResult<string?>("tok-123"), http: new HttpClient(handler));

        var release = Assert.Single(await source.GetReleasesAsync());
        var asset = Asset(release, "app-osx-arm64");

        // Private repos download via the asset API url, not browser_download_url.
        Assert.Equal("https://api.github.com/repos/agocke/app/releases/assets/99", asset.Location);
        Assert.Equal("Bearer tok-123", seenAuth);

        await using var _ = await source.OpenAssetAsync(asset);
        Assert.Contains("application/octet-stream", seenAccept);
        Assert.Equal("Bearer tok-123", seenAuth);
    }

    private static UpdateAsset Asset(UpdateRelease release, string name) =>
        release.Assets.Single(a => a.Name == name);

    private static UpdaterOptions Options(bool allowNonSingleFile = false) => new()
    {
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

    private sealed class FakeSource : IUpdateSource
    {
        private readonly IReadOnlyList<UpdateRelease> _releases;
        private readonly string _assetBody;

        public FakeSource(UpdateRelease release, string assetBody = "")
            : this([release], assetBody) { }

        public FakeSource(params UpdateRelease[] releases)
            : this(releases, "") { }

        private FakeSource(IReadOnlyList<UpdateRelease> releases, string assetBody)
        {
            _releases = releases;
            _assetBody = assetBody;
        }

        public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult(_releases);

        public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(_assetBody)));
    }

    private sealed class ThrowingSource : IUpdateSource
    {
        public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken ct = default) =>
            throw new HttpRequestException("backend down");

        public Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default) =>
            throw new HttpRequestException("backend down");
    }
}
