using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SelfUpdate;

/// <summary>Result of comparing the running build against a source's latest release.</summary>
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
    /// <summary>The version the running process reports as its own.</summary>
    public required SemVer CurrentVersion { get; init; }

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
/// Source-agnostic self-update engine, modeled on dnvm: check a version, download
/// the matching asset, verify and validate it, then hand off to the new binary
/// which replaces the old one in place (a two-process swap so a running
/// executable can update itself, including on Windows).
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

    /// <summary>Reads the informational version of the entry assembly as a <see cref="SemVer"/>.</summary>
    public static SemVer CurrentAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info is not null && SemVer.TryParse(info, out var v))
            return v;
        var name = asm.GetName().Version;
        return name is null ? new SemVer(0, 0, 0) : new SemVer(name.Major, name.Minor, name.Build < 0 ? 0 : name.Build);
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        _log.WriteLine($"Checking for updates (current {_options.CurrentVersion}, rid {_options.Rid})...");
        var release = await _source.GetLatestReleaseAsync(_options.Rid, ct).ConfigureAwait(false);
        if (release is null)
        {
            _log.WriteLine("Update source returned no release information.");
            return new UpdateCheck(false, _options.CurrentVersion, null, null);
        }

        if (release.Version <= _options.CurrentVersion)
        {
            _log.WriteLine($"Up to date (latest {release.Version}).");
            return new UpdateCheck(false, _options.CurrentVersion, release.Version, release.Asset);
        }

        _log.WriteLine($"Update available: {release.Version}");
        return new UpdateCheck(true, _options.CurrentVersion, release.Version, release.Asset);
    }

    public async Task<UpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        var check = await CheckAsync(ct).ConfigureAwait(false);
        if (!check.UpdateAvailable)
            return new UpdateResult(UpdateOutcome.UpToDate, check.Latest);

        if (check.Asset is null)
        {
            _log.WriteLine($"No asset for rid '{_options.Rid}' in release {check.Latest}.");
            return new UpdateResult(UpdateOutcome.NoAssetForPlatform, check.Latest,
                $"No artifact for {_options.Rid}.");
        }

        if (!_options.AllowNonSingleFile && !Utilities.IsSingleFile())
        {
            _log.WriteLine("Cannot self-update: not deployed as a single file (e.g. running via 'dotnet run').");
            return new UpdateResult(UpdateOutcome.NotSelfContained, check.Latest);
        }

        var target = _options.TargetPath ?? Utilities.ProcessPath;
        if (string.IsNullOrEmpty(target))
        {
            _log.WriteLine("Cannot self-update: unable to determine the target executable path.");
            return new UpdateResult(UpdateOutcome.Failed, check.Latest, "Unknown target path.");
        }

        var staged = await DownloadAndValidateAsync(check.Asset, target, ct).ConfigureAwait(false);
        if (staged is null)
            return new UpdateResult(UpdateOutcome.Failed, check.Latest, "Download or validation failed.");

        if (!LaunchHandoff(staged, target))
            return new UpdateResult(UpdateOutcome.Failed, check.Latest, "Could not launch the handoff process.");

        return new UpdateResult(UpdateOutcome.Staged, check.Latest);
    }

    private async Task<string?> DownloadAndValidateAsync(UpdateAsset asset, string target, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "selfupdate-" + Path.GetRandomFileName());
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
