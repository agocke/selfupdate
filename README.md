# SelfUpdater

A small, pluggable self-update engine for single-file / Native AOT .NET apps.

It does what [dnvm](https://github.com/dn-vm/dnvm) does for itself, generalized:
check the running version against a source, download the build matching the
current RID, verify (SHA-256) and validate (smoke-test) it, then replace the
running executable **in place** via a two-process handoff so an app can update
itself — including on Windows, where a running image can't overwrite itself.

## Install

```
dotnet add package SelfUpdater
```

Targets `net8.0`, trim/AOT-compatible, serializes with [Serde.NET](https://github.com/serde-dotnet/serde).

## Where updates come from is pluggable

Everything hangs off one seam, `IUpdateSource`:

```csharp
public interface IUpdateSource
{
    Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(string rid, CancellationToken ct = default);
    Task<Stream> OpenAssetAsync(UpdateAsset asset, CancellationToken ct = default);
}
```

A source only ever *lists* what builds exist — it never compares versions or
decides what counts as "new". That policy stays with the caller, which knows its
own current version.

Backends in the box:

| Source | Use it for |
|---|---|
| `HttpManifestUpdateSource` | A `releases.json` you publish yourself (CDN, Pages, blob store). Relative or absolute asset URLs. |
| `DirectoryUpdateSource` | A local folder or network share — LAN/offline/air-gapped rollouts, and tests. |
| `GitHubReleaseUpdateSource` | GitHub Releases. Public repos need nothing; **private** repos take an `authToken` delegate and download through the authenticated asset API. |
| `CompositeUpdateSource` | Several backends at once: queries all reachable ones and merges their releases, then routes the download back to the backend that produced the chosen asset. Free fallback + mirrors. |

Implementing your own (S3, a package feed, a torrent, ...) is just those two methods.

## Usage

The engine is **policy-free**: you tell it your current version (it never fetches
it for you), and it decides nothing about what is "new" unless you ask it to.

```csharp
using SelfUpdater;
using SelfUpdater.Sources;

var source = new HttpManifestUpdateSource(new Uri("https://example.com/releases.json"));

var updater = new Updater(source, new UpdaterOptions
{
    // Rid defaults to RuntimeInformation.RuntimeIdentifier
    // TargetPath defaults to the running executable
});

// You own the current version and the comparison.
SemVer current = SemVer.Parse(MyApp.Version);

var result = await updater.UpdateAsync(current); // newest-wins convenience overload
if (result.Outcome == UpdateOutcome.Staged)
    return 0; // a newer build was handed off; this process should now exit
```

Need full control over which release to apply (skip prereleases, pin a channel,
roll back, ...)? Drop to the policy-free core:

```csharp
var releases = await updater.GetReleasesAsync();
var chosen = releases
    .Where(r => r.Version > current && !r.Version.IsPrerelease)
    .OrderByDescending(r => r.Version)
    .FirstOrDefault();

if (chosen is not null)
    await updater.ApplyAsync(chosen);
```

### The handoff command

`UpdateAsync` downloads + validates the new binary, then launches it with a
hidden command so the **new** process performs the swap once the old one exits.
Wire that command up once:

```csharp
// e.g. with System.CommandLine — names come from Updater constants
// Updater.HandoffVerb ("apply-update"), Updater.DestOption ("--dest"),
// Updater.PidOption ("--pid"), Updater.RelaunchOption ("--relaunch")
if (args is [Updater.HandoffVerb, ..])
{
    return Updater.ApplySwap(destPath, oldPid, relaunchArgs: null);
}
```

### Private GitHub repos

The library is auth-mechanism agnostic — `authToken` is a
`Func<CancellationToken, Task<string?>>` awaited per request, so a consumer can
fetch/cache/refresh short-lived or rotating tokens however it likes:

```csharp
var gh = new GitHubReleaseUpdateSource(
    owner: "you", repo: "private-app",
    authToken: ct => TokenCache.GetCurrentInstallationTokenAsync(ct)); // your concern
```

Use a fine-grained PAT (`Contents: read-only`), a GitHub App installation token
vended from a small endpoint, or an OAuth device-flow token — the library doesn't
care which.

## License

MIT
