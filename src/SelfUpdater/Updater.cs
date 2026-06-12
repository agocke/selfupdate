using System.Diagnostics;
using System.Runtime.InteropServices;
using Semver;

namespace SelfUpdater;

/// <summary>
/// A raw, source-provided build artifact, identified by the publisher's own
/// <see cref="Name"/> (a GitHub asset file name, a directory file name, ...). The
/// name is interpreted only by the updater's naming convention (see
/// <see cref="AssetNameParser"/>). <see cref="Location"/> is opaque to the shared
/// engine and interpreted only by the updater that produced it (a URL for
/// <see cref="GitHubUpdater"/>, a file path for <see cref="DirectoryUpdater"/>).
/// </summary>
/// <param name="Name">The source's own asset name, fed to the naming convention.</param>
/// <param name="Location">Where the asset can be opened from (path or URL).</param>
/// <param name="Sha256">Optional integrity hash, if the source published one.</param>
/// <param name="Size">Optional asset size in bytes, if known.</param>
/// <param name="IsPrerelease">
/// A source-side prerelease signal (e.g. GitHub's <c>prerelease</c> flag) OR-ed into
/// the resulting release's <see cref="Release.IsPrerelease"/>, on top of whatever the
/// parsed version itself indicates.
/// </param>
public sealed record SourceAsset(
    string Name,
    string Location,
    string? Sha256 = null,
    long? Size = null,
    bool IsPrerelease = false
);

/// <summary>A release for the configured platform, parsed from a source's assets.</summary>
/// <param name="Version">The release version, parsed from the asset's name.</param>
/// <param name="Asset">The raw asset for the configured <see cref="UpdaterOptions.Rid"/>.</param>
/// <param name="IsPrerelease">
/// True when the publisher marked this a prerelease (GitHub's <c>prerelease</c> flag,
/// or a SemVer prerelease suffix). Surfaced so a consumer can choose to skip
/// prereleases via <see cref="UpdaterOptions.ReleaseFilter"/>.
/// </param>
public sealed record Release(SemVersion Version, SourceAsset Asset, bool IsPrerelease = false);

/// <summary>Result of comparing the running build against a source's releases.</summary>
public sealed record UpdateCheck(bool UpdateAvailable, SemVersion Current, Release? Latest);

public enum UpdateOutcome
{
    /// <summary>Already on the latest version (or newer).</summary>
    UpToDate,

    /// <summary>A newer build was downloaded, validated, and handed off; the caller should now exit.</summary>
    Staged,

    /// <summary>The source has builds, but none for the configured RID.</summary>
    NoAssetForPlatform,

    /// <summary>The running process is not a self-contained single file, so it cannot replace itself.</summary>
    NotSelfContained,

    /// <summary>The update could not be completed (network, checksum, validation, ...).</summary>
    Failed,
}

public sealed record UpdateResult(
    UpdateOutcome Outcome,
    SemVersion? Version = null,
    string? Message = null
);

/// <summary>
/// Everything an updater needs: the app's identity and version, the platform to
/// target, how asset names map to releases, and how to stage the swap. Build one up,
/// hand it to a concrete updater (e.g. <c>new GitHubUpdater(owner, repo, options)</c>),
/// then call <c>UpdateAsync</c> / <c>CheckAsync</c>.
/// </summary>
public sealed record UpdaterOptions
{
    /// <summary>
    /// Application name for the default <c>{appName}-{version}-{rid}</c> naming
    /// convention. Ignored when a custom <see cref="Parser"/> is supplied.
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// The version the app is currently running. The updater never fetches this for
    /// you; you own it. A release is "newer" when its version has higher precedence.
    /// </summary>
    public required SemVersion CurrentVersion { get; init; }

    /// <summary>
    /// Target runtime identifier (e.g. <c>osx-arm64</c>): the suffix the default
    /// convention selects on, and what makes the otherwise-ambiguous
    /// <c>{version}-{rid}</c> tail splittable. Defaults to the running platform's
    /// <see cref="RuntimeInformation.RuntimeIdentifier"/>.
    /// </summary>
    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// Optional override for how a raw asset name maps to <c>(version, rid)</c>. When
    /// omitted the default <c>{appName}-{version}-{rid}</c> convention is used. Only
    /// assets whose mapped rid equals <see cref="Rid"/> are kept.
    /// </summary>
    public AssetNameParser? Parser { get; init; }

    /// <summary>
    /// Optional predicate restricting which releases are considered (e.g.
    /// <c>r =&gt; !r.IsPrerelease</c> to ignore prereleases). When <c>null</c> every
    /// release for the platform is a candidate.
    /// </summary>
    public Func<Release, bool>? ReleaseFilter { get; init; }

    /// <summary>Path of the binary to replace. Defaults to the running executable.</summary>
    public string? TargetPath { get; init; }

    /// <summary>
    /// Arguments used to smoke-test a freshly downloaded binary, which must exit 0.
    /// Validation is <b>opt-in</b>: when <c>null</c> or empty (the default) the
    /// downloaded binary is not executed before being staged. Set this only if your
    /// binary supports the given arguments and exits 0 on success (e.g.
    /// <c>["--version"]</c>) — there is no universally safe probe to assume.
    /// </summary>
    public IReadOnlyList<string>? ValidateArgs { get; init; }

    /// <summary>When true, the handed-off process relaunches the app after swapping.</summary>
    public bool Relaunch { get; init; }

    /// <summary>Allow updating even when not deployed as a single file (e.g. for tests).</summary>
    public bool AllowNonSingleFile { get; init; }

    public TextWriter Log { get; init; } = Console.Out;
}

/// <summary>
/// Source-agnostic self-update engine, modeled on dnvm. A concrete updater
/// (<see cref="GitHubUpdater"/>, <see cref="DirectoryUpdater"/>) supplies just two
/// things — how to list a source's raw assets and how to open one's bytes — and this
/// base does the rest: turn asset names into releases for the configured platform,
/// pick the newest, download and verify it, then hand off to the new binary which
/// replaces the old one in place (a two-process swap so a running executable can
/// update itself, including on Windows).
/// <para>
/// The caller owns its current version (<see cref="UpdaterOptions.CurrentVersion"/>);
/// the engine owns naming, platform selection, and the "newest wins" comparison.
/// </para>
/// </summary>
public abstract class Updater
{
    // Wire contract for the handoff (new-process) side. The host app registers a
    // command/handler with these exact names; keeping them here makes this the
    // single source of truth shared by both processes.
    public const string HandoffVerb = "apply-update";
    public const string DestOption = "--dest";
    public const string PidOption = "--pid";
    public const string RelaunchOption = "--relaunch";

    private readonly UpdaterOptions _options;
    private readonly AssetNameParser _parse;
    private readonly TextWriter _log;

    protected Updater(UpdaterOptions options)
    {
        _options = options;
        _parse = options.Parser ?? AssetNaming.DefaultParser(options.AppName, options.Rid);
        _log = options.Log;
    }

    /// <summary>
    /// List every raw artifact this source can see, with whatever metadata it knows
    /// (location, integrity hash, size, prerelease hint). Returns an empty list if the
    /// source could not be reached or parsed. Order is not significant.
    /// </summary>
    internal abstract Task<IReadOnlyList<SourceAsset>> GetAssetsAsync(CancellationToken ct);

    /// <summary>Open a read stream over an asset's bytes for downloading.</summary>
    internal abstract Task<Stream> OpenAssetAsync(SourceAsset asset, CancellationToken ct);

    /// <summary>
    /// List the releases available for the configured platform, with asset names
    /// parsed into versions and filtered to <see cref="UpdaterOptions.Rid"/>. Internal
    /// helper behind <see cref="CheckAsync"/> / <see cref="UpdateAsync"/> (and a
    /// naming/selection testing seam); applies no version comparison and no
    /// <see cref="UpdaterOptions.ReleaseFilter"/>.
    /// </summary>
    internal async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        var assets = await GetAssetsAsync(ct).ConfigureAwait(false);
        return AssetNaming.ToReleases(assets, _parse, _options.Rid);
    }

    /// <summary>
    /// Report whether any release is newer than
    /// <see cref="UpdaterOptions.CurrentVersion"/>, returning the newest one (after
    /// applying <see cref="UpdaterOptions.ReleaseFilter"/>).
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var current = _options.CurrentVersion;
        _log.WriteLine($"Checking for updates (current {current})...");
        var releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        var newest = Newest(releases, _options.ReleaseFilter);
        if (newest is null)
        {
            _log.WriteLine("Update source returned no release information.");
            return new UpdateCheck(false, current, null);
        }

        var available = newest.Version.ComparePrecedenceTo(current) > 0;
        _log.WriteLine(
            available
                ? $"Update available: {newest.Version}"
                : $"Up to date (latest {newest.Version})."
        );
        return new UpdateCheck(available, current, newest);
    }

    /// <summary>
    /// Pick the newest release (after <see cref="UpdaterOptions.ReleaseFilter"/>); if it
    /// is newer than <see cref="UpdaterOptions.CurrentVersion"/>, download, validate, and
    /// stage its asset, then hand off. If the source offers builds but none for the
    /// configured RID, reports <see cref="UpdateOutcome.NoAssetForPlatform"/>;
    /// otherwise <see cref="UpdateOutcome.UpToDate"/>.
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        var assets = await GetAssetsAsync(ct).ConfigureAwait(false);
        var releases = AssetNaming.ToReleases(assets, _parse, _options.Rid);
        var newest = Newest(releases, _options.ReleaseFilter);
        if (newest is null)
        {
            if (assets.Count > 0)
            {
                _log.WriteLine($"Source has builds, but none for {_options.Rid}.");
                return new UpdateResult(
                    UpdateOutcome.NoAssetForPlatform,
                    null,
                    $"No asset for {_options.Rid}."
                );
            }
            _log.WriteLine("Update source returned no release information.");
            return new UpdateResult(UpdateOutcome.UpToDate);
        }

        if (newest.Version.ComparePrecedenceTo(_options.CurrentVersion) <= 0)
            return new UpdateResult(UpdateOutcome.UpToDate, newest.Version);

        return await ApplyAssetAsync(newest.Asset, newest.Version, ct).ConfigureAwait(false);
    }

    private async Task<UpdateResult> ApplyAssetAsync(
        SourceAsset asset,
        SemVersion version,
        CancellationToken ct
    )
    {
        if (!_options.AllowNonSingleFile && !Utilities.IsSingleFile())
        {
            _log.WriteLine(
                "Cannot self-update: not deployed as a single file (e.g. running via 'dotnet run')."
            );
            return new UpdateResult(UpdateOutcome.NotSelfContained, version);
        }

        var target = _options.TargetPath ?? Utilities.ProcessPath;
        if (string.IsNullOrEmpty(target))
        {
            _log.WriteLine("Cannot self-update: unable to determine the target executable path.");
            return new UpdateResult(UpdateOutcome.Failed, version, "Unknown target path.");
        }

        var staged = await DownloadAndValidateAsync(asset, target, ct).ConfigureAwait(false);
        if (staged is null)
            return new UpdateResult(
                UpdateOutcome.Failed,
                version,
                "Download or validation failed."
            );

        if (!LaunchHandoff(staged, target))
            return new UpdateResult(
                UpdateOutcome.Failed,
                version,
                "Could not launch the handoff process."
            );

        return new UpdateResult(UpdateOutcome.Staged, version);
    }

    /// <summary>Pick the highest-version release, after applying an optional filter.</summary>
    private static Release? Newest(IReadOnlyList<Release> releases, Func<Release, bool>? filter)
    {
        Release? best = null;
        foreach (var release in releases)
        {
            if (filter is not null && !filter(release))
                continue;
            if (best is null || release.Version.ComparePrecedenceTo(best.Version) > 0)
                best = release;
        }
        return best;
    }

    private async Task<string?> DownloadAndValidateAsync(
        SourceAsset asset,
        string target,
        CancellationToken ct
    )
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "selfupdater-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        // Name the staged file after the target so the swapped-in binary keeps its name.
        var staged = Path.Combine(tempDir, Path.GetFileName(target));

        _log.WriteLine($"Downloading {asset.Location}...");
        try
        {
            await using var src = await OpenAssetAsync(asset, ct).ConfigureAwait(false);
            await using var file = new FileStream(
                staged,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            await src.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.WriteLine($"Download failed: {e.Message}");
            return null;
        }

        if (!string.IsNullOrEmpty(asset.Sha256))
        {
            var actual = await Utilities.ComputeSha256Async(staged, ct).ConfigureAwait(false);
            if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.WriteLine($"Checksum mismatch: expected {asset.Sha256}, got {actual}.");
                return null;
            }
            _log.WriteLine("Checksum OK.");
        }

        if (!OperatingSystem.IsWindows())
            Utilities.MakeExecutable(staged);

        if (!await ValidateAsync(staged, ct).ConfigureAwait(false))
            return null;

        return staged;
    }

    private async Task<bool> ValidateAsync(string path, CancellationToken ct)
    {
        // Validation is opt-in: with no args configured we do not execute the
        // freshly downloaded binary, since there is no universally safe probe.
        if (_options.ValidateArgs is not { Count: > 0 } validateArgs)
            return true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in validateArgs)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.WriteLine("Could not start the downloaded binary for validation.");
                return false;
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                _log.WriteLine($"Downloaded binary failed validation (exit {proc.ExitCode}).");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            _log.WriteLine($"Validation error: {e.Message}");
            return false;
        }
    }

    private bool LaunchHandoff(string stagedPath, string target)
    {
        _log.WriteLine($"Staged update ready; handing off to replace {target}.");
        var psi = new ProcessStartInfo
        {
            FileName = stagedPath,
            ArgumentList =
            {
                HandoffVerb,
                DestOption,
                target,
                PidOption,
                Environment.ProcessId.ToString(),
            },
        };
        if (_options.Relaunch)
            psi.ArgumentList.Add(RelaunchOption);

        return Process.Start(psi) is not null;
    }

    /// <summary>
    /// The handoff (new-process) side of the swap. Runs from the freshly downloaded
    /// binary: waits for the previous process to exit, replaces the target with
    /// itself, and optionally relaunches. Wire this up in your entry point under
    /// <see cref="HandoffVerb"/>.
    /// </summary>
    public static int ApplySwap(
        string destPath,
        int oldPid,
        IReadOnlyList<string>? relaunchArgs = null,
        TextWriter? log = null
    )
    {
        log ??= Console.Out;

        if (oldPid > 0)
        {
            try
            {
                using var old = Process.GetProcessById(oldPid);
                log.WriteLine($"Waiting for previous process (pid {oldPid}) to exit...");
                if (!old.WaitForExit(30_000))
                    log.WriteLine("Previous process did not exit in time; attempting swap anyway.");
            }
            catch (ArgumentException)
            {
                // Process already gone.
            }
        }

        var src = Utilities.ProcessPath;
        if (string.IsNullOrEmpty(src))
        {
            log.WriteLine("Cannot apply update: unknown source path.");
            return 1;
        }

        var backup = destPath + ".bak";
        try
        {
            if (File.Exists(destPath))
                File.Move(destPath, backup, overwrite: true);

            // Copy (rather than move) the still-running staged binary so the swap
            // works across volumes and the running image stays valid.
            File.Copy(src, destPath, overwrite: true);
            File.SetLastWriteTimeUtc(destPath, DateTime.UtcNow);
            if (!OperatingSystem.IsWindows())
                Utilities.MakeExecutable(destPath);

            if (File.Exists(backup))
            {
                try
                {
                    File.Delete(backup);
                }
                catch
                { /* a locked .bak on Windows is harmless; leave it for next run */
                }
            }
        }
        catch (Exception e)
        {
            log.WriteLine($"Swap failed: {e.Message}");
            if (File.Exists(backup) && !File.Exists(destPath))
            {
                try
                {
                    File.Move(backup, destPath);
                }
                catch
                { /* best effort */
                }
            }
            return 1;
        }

        log.WriteLine($"Updated in place: {destPath}");

        if (relaunchArgs is not null)
        {
            log.WriteLine("Relaunching...");
            var psi = new ProcessStartInfo { FileName = destPath };
            foreach (var a in relaunchArgs)
                psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        return 0;
    }
}
