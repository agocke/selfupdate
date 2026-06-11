using System.Net;
using System.Text;
using SelfUpdater;
using SelfUpdater.Sources;
using Semver;

namespace SelfUpdater.Tests;

public class SelfUpdaterTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v0.1.0", 0, 1, 0, "")]
    [InlineData("2.0", 2, 0, 0, "")]
    [InlineData("1.2.3-beta.1+build7", 1, 2, 3, "beta.1")]
    public void Version_ParsesLenientForms(string text, int major, int minor, int patch, string pre)
    {
        Assert.True(SemVersion.TryParse(text, SemVersionStyles.Any, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.Prerelease);
        Assert.Equal(pre.Length > 0, v.IsPrerelease);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("")]
    public void Version_RejectsInvalid(string text) =>
        Assert.False(SemVersion.TryParse(text, SemVersionStyles.Any, out _));

    [Fact]
    public void Version_OrdersByPrecedence()
    {
        Assert.True(V("1.0.0").ComparePrecedenceTo(V("1.0.1")) < 0);
        Assert.True(V("1.2.0").ComparePrecedenceTo(V("1.1.9")) > 0);
        // Prerelease sorts below its release.
        Assert.True(V("1.0.0-beta").ComparePrecedenceTo(V("1.0.0")) < 0);
        Assert.True(V("1.0.0-alpha").ComparePrecedenceTo(V("1.0.0-beta")) < 0);
        Assert.True(V("1.0.0-alpha.1").ComparePrecedenceTo(V("1.0.0-alpha.2")) < 0);
        // Build metadata does not affect precedence.
        Assert.Equal(0, V("1.0.0+a").ComparePrecedenceTo(V("1.0.0+b")));
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
                  { "name": "app-0.3.0-osx-arm64", "browser_download_url": "https://dl/osx-030", "size": 42,
                    "digest": "sha256:DEADBEEF" },
                  { "name": "app-0.3.0-linux-x64", "browser_download_url": "https://dl/linux-030" }
                ]
              },
              {
                "tag_name": "v0.2.0",
                "assets": [
                  { "name": "app-0.2.0-osx-arm64", "browser_download_url": "https://dl/osx-020" }
                ]
              }
            ]
            """;
        var source = new GitHubReleaseUpdateSource("agocke", "app", "app", http: StubClient(json));

        var releases = await source.GetReleasesAsync();

        Assert.Equal(2, releases.Count);
        var v030 = releases.Single(r => r.Version == V("0.3.0"));
        Assert.Equal(2, v030.Assets.Count);
        // The asset Name is the rid parsed out of the file name.
        var osx = FindAsset(v030, "osx-arm64");
        Assert.Equal("https://dl/osx-030", osx.Location);
        Assert.Equal(42, osx.Size);
        // GitHub's published digest becomes the integrity hash.
        Assert.Equal("DEADBEEF", osx.Sha256);
        Assert.Null(FindAsset(v030, "linux-x64").Sha256);
    }

    [Fact]
    public async Task GitHubSource_SkipsDraftsAndFlagsPrereleases()
    {
        const string json = """
            [
              { "tag_name": "v0.4.0", "draft": true,
                "assets": [ { "name": "app-0.4.0-osx-arm64", "browser_download_url": "https://dl/draft" } ] },
              { "tag_name": "v0.3.0", "prerelease": true,
                "assets": [ { "name": "app-0.3.0-osx-arm64", "browser_download_url": "https://dl/pre" } ] },
              { "tag_name": "v0.2.0",
                "assets": [ { "name": "app-0.2.0-osx-arm64", "browser_download_url": "https://dl/rel" } ] }
            ]
            """;
        var source = new GitHubReleaseUpdateSource("agocke", "app", "app", http: StubClient(json));

        var releases = await source.GetReleasesAsync();

        // The draft is gone; the prerelease is present and flagged.
        Assert.Equal(2, releases.Count);
        Assert.DoesNotContain(releases, r => r.Version == V("0.4.0"));
        Assert.True(releases.Single(r => r.Version == V("0.3.0")).IsPrerelease);
        Assert.False(releases.Single(r => r.Version == V("0.2.0")).IsPrerelease);
    }

    [Fact]
    public async Task DirectorySource_ListsBinariesByConventionAndOpensAsset()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdater-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "myapp-1.5.0-osx-arm64"), "the-binary-bytes");
            File.WriteAllText(Path.Combine(dir, "myapp-1.5.0-linux-x64"), "linux-bytes");
            // Sidecar checksum (sha256sum format) is picked up for the osx binary only.
            File.WriteAllText(
                Path.Combine(dir, "myapp-1.5.0-osx-arm64.sha256"),
                "abc123  myapp-1.5.0-osx-arm64\n"
            );
            // Unrelated files are ignored.
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignore me");

            var source = new DirectoryUpdateSource(dir, "myapp");

            var release = Assert.Single(await source.GetReleasesAsync());

            Assert.Equal(V("1.5.0"), release.Version);
            Assert.Equal(2, release.Assets.Count);

            var osx = FindAsset(release, "osx-arm64");
            Assert.Equal(Path.Combine(dir, "myapp-1.5.0-osx-arm64"), osx.Location);
            Assert.Equal("abc123", osx.Sha256);
            Assert.Null(FindAsset(release, "linux-x64").Sha256);

            await using var stream = await source.OpenAssetAsync(osx);
            using var reader = new StreamReader(stream);
            Assert.Equal("the-binary-bytes", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_GroupsReleasesAndFlagsPrereleasesViaCustomParser()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdater-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "myapp-2.0.0-osx-arm64"), "stable");
            File.WriteAllText(Path.Combine(dir, "myapp-3.0.0-rc.1-osx-arm64"), "rc");

            // Custom parser handles the prerelease tag the default convention can't split.
            var source = new DirectoryUpdateSource(
                dir,
                "myapp",
                fileName =>
                {
                    const string prefix = "myapp-";
                    if (!fileName.StartsWith(prefix) || !fileName.EndsWith("-osx-arm64"))
                        return null;
                    var version = fileName[prefix.Length..^"-osx-arm64".Length];
                    return SemVersion.TryParse(version, SemVersionStyles.Any, out var v)
                        ? (v, "osx-arm64")
                        : null;
                }
            );

            var releases = await source.GetReleasesAsync();

            Assert.Equal(2, releases.Count);
            Assert.True(releases.Single(r => r.Version == V("3.0.0-rc.1")).IsPrerelease);
            Assert.False(releases.Single(r => r.Version == V("2.0.0")).IsPrerelease);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_MissingDirectoryReturnsEmpty()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "selfupdater-missing-" + Path.GetRandomFileName()
        );
        var source = new DirectoryUpdateSource(dir, "myapp");
        Assert.Empty(await source.GetReleasesAsync());
    }

    [Fact]
    public async Task Updater_DetectsAvailableAndUpToDate()
    {
        var newer = new FakeSource(new Release(V("9.9.9"), [new Asset("app", "https://dl")]));
        var available = await new Updater(newer, Options()).CheckAsync(V("1.0.0"));
        Assert.True(available.UpdateAvailable);
        Assert.Equal(V("9.9.9"), available.Latest!.Version);

        var same = new FakeSource(new Release(V("1.0.0"), [new Asset("app", "https://dl")]));
        var upToDate = await new Updater(same, Options()).CheckAsync(V("1.0.0"));
        Assert.False(upToDate.UpdateAvailable);
    }

    [Fact]
    public async Task Updater_PicksNewestAcrossMultipleReleases()
    {
        var source = new FakeSource(
            new Release(V("1.0.0"), [new Asset("app", "v1")]),
            new Release(V("3.0.0"), [new Asset("app", "v3")]),
            new Release(V("2.0.0"), [new Asset("app", "v2")])
        );

        var check = await new Updater(source, Options()).CheckAsync(V("1.5.0"));

        Assert.True(check.UpdateAvailable);
        Assert.Equal(V("3.0.0"), check.Latest!.Version);
    }

    [Fact]
    public async Task Updater_ReleaseFilterExcludesPrereleases()
    {
        var source = new FakeSource(
            new Release(V("2.0.0"), [new Asset("app", "stable")]),
            new Release(V("3.0.0-rc.1"), [new Asset("app", "rc")], IsPrerelease: true)
        );

        // Without a filter the prerelease is newest; with one it is skipped.
        var all = await new Updater(source, Options()).CheckAsync(V("1.0.0"));
        Assert.Equal(V("3.0.0-rc.1"), all.Latest!.Version);

        var stableOnly = await new Updater(source, Options()).CheckAsync(
            V("1.0.0"),
            r => !r.IsPrerelease
        );
        Assert.Equal(V("2.0.0"), stableOnly.Latest!.Version);
    }

    [Fact]
    public async Task Updater_ReportsNoAssetForPlatform()
    {
        // A newer release exists but ships nothing the selector accepts.
        var source = new FakeSource(new Release(V("2.0.0"), [new Asset("win-x64", "loc")]));
        var result = await new Updater(source, Options(allowNonSingleFile: true)).UpdateAsync(
            V("1.0.0"),
            a => a.Name == "osx-arm64"
        );
        Assert.Equal(UpdateOutcome.NoAssetForPlatform, result.Outcome);
    }

    [Fact]
    public async Task Updater_UpToDateWhenNoNewerRelease()
    {
        var source = new FakeSource(new Release(V("1.0.0"), [new Asset("osx-arm64", "loc")]));
        var result = await new Updater(source, Options(allowNonSingleFile: true)).UpdateAsync(
            V("1.0.0"),
            a => a.Name == "osx-arm64"
        );
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
                    "name": "app-0.3.0-osx-arm64",
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
        var handler = new CapturingHandler(
            json,
            req =>
            {
                seenAuth = req.Headers.Authorization?.ToString();
                seenAccept = req.Headers.Accept.ToString();
            }
        );
        var source = new GitHubReleaseUpdateSource(
            "agocke",
            "app",
            "app",
            authToken: _ => Task.FromResult<string?>("tok-123"),
            http: new HttpClient(handler)
        );

        var release = Assert.Single(await source.GetReleasesAsync());
        var asset = FindAsset(release, "osx-arm64");

        // Private repos download via the asset API url, not browser_download_url.
        Assert.Equal("https://api.github.com/repos/agocke/app/releases/assets/99", asset.Location);
        Assert.Equal("Bearer tok-123", seenAuth);

        await using var _ = await source.OpenAssetAsync(asset);
        Assert.Contains("application/octet-stream", seenAccept);
        Assert.Equal("Bearer tok-123", seenAuth);
    }

    private static Asset FindAsset(Release release, string name) =>
        release.Assets.Single(a => a.Name == name);

    private static SemVersion V(string value) => SemVersion.Parse(value, SemVersionStyles.Any);

    private static UpdaterOptions Options(bool allowNonSingleFile = false) =>
        new()
        {
            Relaunch = false,
            AllowNonSingleFile = allowNonSingleFile,
            Log = TextWriter.Null,
        };

    private static HttpClient StubClient(string body) => new(new StubHandler(body));

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
    }

    private sealed class CapturingHandler(string body, Action<HttpRequestMessage> capture)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            capture(request);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private sealed class FakeSource : IUpdateSource
    {
        private readonly IReadOnlyList<Release> _releases;
        private readonly string _assetBody;

        public FakeSource(Release release, string assetBody = "")
            : this([release], assetBody) { }

        public FakeSource(params Release[] releases)
            : this(releases, "") { }

        private FakeSource(IReadOnlyList<Release> releases, string assetBody)
        {
            _releases = releases;
            _assetBody = assetBody;
        }

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult(_releases);

        public Task<Stream> OpenAssetAsync(Asset asset, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(_assetBody)));
    }
}
