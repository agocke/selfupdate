using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SelfUpdater;

/// <summary>Result of comparing the running build against a source's releases.</summary>
public sealed record UpdateCheck(bool UpdateAvailable, SemVer Current, SemVer? Latest, UpdateAsset? Asset);

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

public sealed record UpdateResult(UpdateOutcome Outcome, SemVer? Version = null, string? Message = null);

public sealed class UpdaterOptions
{
    /// <summary>Runtime identifier to request from the source (defaults to the running RID).</summary>
    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    /// <summary>Path of the binary to replace. Defaults to the running executable.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Arguments used to smoke-test a freshly downloaded binary; must exit 0.</summary>
    public IReadOnlyList<string> ValidateArgs { get; init; } = ["--help"];

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
/// caller's current version, and it never decides what counts as "new". The caller
/// owns its version and the comparison. Use <see cref="GetReleasesAsync"/> +
/// <see cref="ApplyAsync(UpdateRelease, CancellationToken)"/> for full control, or
/// the <see cref="UpdateAsync(SemVer, CancellationToken)"/> / <see cref="CheckAsync(SemVer, CancellationToken)"/>
/// convenience overloads (which take the caller's current version and apply the
/// common "newest wins" policy) for the simple case.
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
    /// List the releases the source offers for this RID. The caller compares them
    /// against its own current version to decide whether any are new; this method
    /// applies no policy of its own.
    /// </summary>
    public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken ct = default) =>
        _source.GetReleasesAsync(_options.Rid, ct);

    /// <summary>
    /// Download, validate, and stage a release the caller has chosen, then hand off
    /// to the new binary. Performs <b>no</b> version comparison — it applies exactly
    /// the release it is given. The caller is responsible for having decided this
    /// release is newer than what it is running.
    /// </summary>
    public async Task<UpdateResult> ApplyAsync(UpdateRelease release, CancellationToken ct = default)
    {
        if (release.Asset is null)
        {
            _log.WriteLine($"No asset for rid '{_options.Rid}' in release {release.Version}.");
            return new UpdateResult(UpdateOutcome.NoAssetForPlatform, release.Version,
                $"No artifact for {_options.Rid}.");
        }

        if (!_options.AllowNonSingleFile && !Utilities.IsSingleFile())
        {
            _log.WriteLine("Cannot self-update: not deployed as a single file (e.g. running via 'dotnet run').");
            return new UpdateResult(UpdateOutcome.NotSelfContained, release.Version);
        }

        var target = _options.TargetPath ?? Utilities.ProcessPath;
        if (string.IsNullOrEmpty(target))
        {
            _log.WriteLine("Cannot self-update: unable to determine the target executable path.");
            return new UpdateResult(UpdateOutcome.Failed, release.Version, "Unknown target path.");
        }

        var staged = await DownloadAndValidateAsync(release.Asset, target, ct).ConfigureAwait(false);
        if (staged is null)
            return new UpdateResult(UpdateOutcome.Failed, release.Version, "Download or validation failed.");

        if (!LaunchHandoff(staged, target))
            return new UpdateResult(UpdateOutcome.Failed, release.Version, "Could not launch the handoff process.");

        return new UpdateResult(UpdateOutcome.Staged, release.Version);
    }

    /// <summary>
    /// Convenience over <see cref="GetReleasesAsync"/>: report whether any release
    /// is newer than <paramref name="currentVersion"/>, returning the newest one.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(SemVer currentVersion, CancellationToken ct = default)
    {
        _log.WriteLine($"Checking for updates (current {currentVersion}, rid {_options.Rid})...");
        var releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        var newest = Newest(releases);
        if (newest is null)
        {
            _log.WriteLine("Update source returned no release information.");
            return new UpdateCheck(false, currentVersion, null, null);
        }

        var available = newest.Version > currentVersion;
        _log.WriteLine(available ? $"Update available: {newest.Version}" : $"Up to date (latest {newest.Version}).");
        return new UpdateCheck(available, currentVersion, newest.Version, newest.Asset);
    }

    /// <summary>
    /// Convenience over <see cref="GetReleasesAsync"/> + <see cref="ApplyAsync(UpdateRelease, CancellationToken)"/>:
    /// pick the newest release; if it is newer than <paramref name="currentVersion"/>,
    /// apply it, otherwise report <see cref="UpdateOutcome.UpToDate"/>.
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(SemVer currentVersion, CancellationToken ct = default)
    {
        var releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        var newest = Newest(releases);
        if (newest is null || newest.Version <= currentVersion)
            return new UpdateResult(UpdateOutcome.UpToDate, newest?.Version);

        return await ApplyAsync(newest, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pick the highest-version release, preferring one that actually has an asset
    /// for this RID when two releases share the same version.
    /// </summary>
    private static UpdateRelease? Newest(IReadOnlyList<UpdateRelease> releases)
    {
        UpdateRelease? best = null;
        foreach (var release in releases)
        {
            if (best is null)
            {
                best = release;
                continue;
            }
            var cmp = release.Version.CompareTo(best.Version);
            if (cmp > 0 || (cmp == 0 && release.Asset is not null && best.Asset is null))
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
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in _options.ValidateArgs)
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
