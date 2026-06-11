using System.Diagnostics;
using Semver;

namespace SelfUpdater;

/// <summary>Result of comparing the running build against a source's releases.</summary>
public sealed record UpdateCheck(bool UpdateAvailable, SemVersion Current, UpdateRelease? Latest);

public enum UpdateOutcome
{
    /// <summary>Already on the latest version (or newer).</summary>
    UpToDate,
    /// <summary>A newer build was downloaded, validated, and handed off; the caller should now exit.</summary>
    Staged,
    /// <summary>A newer version exists but the source had no asset for this RID.</summary>
    NoAssetForPlatform,
    /// <summary>The running process is not a self-contained single file, so it cannot replace itself.</summary>
    NotSelfContained,
    /// <summary>The update could not be completed (network, checksum, validation, ...).</summary>
    Failed,
}

public sealed record UpdateResult(UpdateOutcome Outcome, SemVersion? Version = null, string? Message = null);

public sealed class UpdaterOptions
{
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
/// Source-agnostic self-update engine, modeled on dnvm: list the releases a
/// source offers, download a chosen one's matching asset, verify and validate it,
/// then hand off to the new binary which replaces the old one in place (a
/// two-process swap so a running executable can update itself, including on
/// Windows).
/// <para>
/// The engine is deliberately <b>policy-free</b>: it neither knows nor fetches the
/// caller's current version, never decides what counts as "new", and never picks
/// which asset suits the running platform. The caller owns its version, the version
/// comparison, and asset selection. Use <see cref="GetReleasesAsync"/> +
/// <see cref="ApplyAsync(UpdateAsset, CancellationToken)"/> for full control, or the
/// <see cref="UpdateAsync"/> / <see cref="CheckAsync"/> convenience overloads (which
/// take the caller's current version plus an asset selector and apply the common
/// "newest wins" policy) for the simple case.
/// </para>
/// </summary>
public sealed class Updater
{
    // Wire contract for the handoff (new-process) side. The host app registers a
    // command/handler with these exact names; keeping them here makes this the
    // single source of truth shared by both processes.
    public const string HandoffVerb = "apply-update";
    public const string DestOption = "--dest";
    public const string PidOption = "--pid";
    public const string RelaunchOption = "--relaunch";

    private readonly IUpdateSource _source;
    private readonly UpdaterOptions _options;
    private readonly TextWriter _log;

    public Updater(IUpdateSource source, UpdaterOptions options)
    {
        _source = source;
        _options = options;
        _log = options.Log;
    }

    /// <summary>
    /// List the releases the source offers. The caller compares them against its own
    /// current version to decide whether any are new, and selects the asset matching
    /// its platform; this method applies no policy of its own.
    /// </summary>
    public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken ct = default) =>
        _source.GetReleasesAsync(ct);

    /// <summary>
    /// Download, validate, and stage a specific asset the caller has chosen, then
    /// hand off to the new binary. Performs <b>no</b> version comparison or asset
    /// selection — it applies exactly the asset it is given. The caller is
    /// responsible for having decided this asset is the right, newer build.
    /// </summary>
    public Task<UpdateResult> ApplyAsync(UpdateAsset asset, CancellationToken ct = default) =>
        ApplyAssetAsync(asset, version: null, ct);

    private async Task<UpdateResult> ApplyAssetAsync(UpdateAsset asset, SemVersion? version, CancellationToken ct)
    {
        if (!_options.AllowNonSingleFile && !Utilities.IsSingleFile())
        {
            _log.WriteLine("Cannot self-update: not deployed as a single file (e.g. running via 'dotnet run').");
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
            return new UpdateResult(UpdateOutcome.Failed, version, "Download or validation failed.");

        if (!LaunchHandoff(staged, target))
            return new UpdateResult(UpdateOutcome.Failed, version, "Could not launch the handoff process.");

        return new UpdateResult(UpdateOutcome.Staged, version);
    }

    /// <summary>
    /// Convenience over <see cref="GetReleasesAsync"/>: report whether any release is
    /// newer than <paramref name="currentVersion"/>, returning the newest one.
    /// Pass <paramref name="releaseFilter"/> to restrict the candidates (e.g.
    /// <c>r =&gt; !r.IsPrerelease</c> to ignore prereleases).
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(
        SemVersion currentVersion,
        Func<UpdateRelease, bool>? releaseFilter = null,
        CancellationToken ct = default)
    {
        _log.WriteLine($"Checking for updates (current {currentVersion})...");
        var releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        var newest = Newest(releases, releaseFilter);
        if (newest is null)
        {
            _log.WriteLine("Update source returned no release information.");
            return new UpdateCheck(false, currentVersion, null);
        }

        var available = newest.Version.ComparePrecedenceTo(currentVersion) > 0;
        _log.WriteLine(available ? $"Update available: {newest.Version}" : $"Up to date (latest {newest.Version}).");
        return new UpdateCheck(available, currentVersion, newest);
    }

    /// <summary>
    /// Convenience over <see cref="GetReleasesAsync"/> + <see cref="ApplyAsync(UpdateAsset, CancellationToken)"/>:
    /// pick the newest release (optionally restricted by <paramref name="releaseFilter"/>);
    /// if it is newer than <paramref name="currentVersion"/>, select its asset with
    /// <paramref name="assetSelector"/> and apply it, otherwise report
    /// <see cref="UpdateOutcome.UpToDate"/>. If the newest release ships no asset the
    /// selector accepts, reports <see cref="UpdateOutcome.NoAssetForPlatform"/>.
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(
        SemVersion currentVersion,
        Func<UpdateAsset, bool> assetSelector,
        Func<UpdateRelease, bool>? releaseFilter = null,
        CancellationToken ct = default)
    {
        var releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        var newest = Newest(releases, releaseFilter);
        if (newest is null || newest.Version.ComparePrecedenceTo(currentVersion) <= 0)
            return new UpdateResult(UpdateOutcome.UpToDate, newest?.Version);

        UpdateAsset? asset = null;
        foreach (var candidate in newest.Assets)
        {
            if (assetSelector(candidate))
            {
                asset = candidate;
                break;
            }
        }
        if (asset is null)
        {
            _log.WriteLine($"No matching asset in release {newest.Version} for this platform.");
            return new UpdateResult(UpdateOutcome.NoAssetForPlatform, newest.Version, "No matching asset.");
        }

        return await ApplyAssetAsync(asset, newest.Version, ct).ConfigureAwait(false);
    }

    /// <summary>Pick the highest-version release, after applying an optional filter.</summary>
    private static UpdateRelease? Newest(IReadOnlyList<UpdateRelease> releases, Func<UpdateRelease, bool>? filter)
    {
        UpdateRelease? best = null;
        foreach (var release in releases)
        {
            if (filter is not null && !filter(release))
                continue;
            if (best is null || release.Version.ComparePrecedenceTo(best.Version) > 0)
                best = release;
        }
        return best;
    }

    private async Task<string?> DownloadAndValidateAsync(UpdateAsset asset, string target, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "selfupdater-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        // Name the staged file after the target so the swapped-in binary keeps its name.
        var staged = Path.Combine(tempDir, Path.GetFileName(target));

        _log.WriteLine($"Downloading {asset.Location}...");
        try
        {
            await using var src = await _source.OpenAssetAsync(asset, ct).ConfigureAwait(false);
            await using var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
            ArgumentList = { HandoffVerb, DestOption, target, PidOption, Environment.ProcessId.ToString() },
        };
        if (_options.Relaunch)
            psi.ArgumentList.Add(RelaunchOption);

        return Process.Start(psi) is not null;
    }

    /// <summary>
    /// The handoff (new-process) side of the swap. Runs from the freshly
    /// downloaded binary: waits for the previous process to exit, replaces the
    /// target with itself, and optionally relaunches.
    /// </summary>
    public static int ApplySwap(
        string destPath,
        int oldPid,
        IReadOnlyList<string>? relaunchArgs = null,
        TextWriter? log = null)
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
                try { File.Delete(backup); }
                catch { /* a locked .bak on Windows is harmless; leave it for next run */ }
            }
        }
        catch (Exception e)
        {
            log.WriteLine($"Swap failed: {e.Message}");
            if (File.Exists(backup) && !File.Exists(destPath))
            {
                try { File.Move(backup, destPath); } catch { /* best effort */ }
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
