using System.Diagnostics;
using System.Text;
using GamepadToolkit.Core.Remap;
using Nefarius.Drivers.HidHide;

namespace GamepadToolkit.Core.Setup;

public enum DriverKind
{
    ViGEmBus,
    HidHide
}

/// <summary>One kernel driver the toolkit depends on, and everything needed to obtain it.</summary>
public sealed record DriverRequirement(
    DriverKind Kind,
    string DisplayName,
    string WingetId,
    string Purpose,
    string DownloadUrl,
    bool NeedsRebootAfterInstall);

public sealed record InstallResult(bool Success, int ExitCode, string Output);

/// <summary>
/// Detects the two drivers the remap features need and installs them through winget.
///
/// This exists for the portable build: a copied-in exe has no installer to run prerequisites,
/// so the app has to notice what is missing on first launch and offer to fetch it.
/// </summary>
public static class DriverBootstrap
{
    public static IReadOnlyList<DriverRequirement> All { get; } =
    [
        new(DriverKind.ViGEmBus,
            "ViGEmBus",
            "ViGEm.ViGEmBus",
            "Creates the virtual controller that games read instead of your physical pad. Remapping cannot work without it.",
            "https://github.com/nefarius/ViGEmBus/releases/latest",
            NeedsRebootAfterInstall: false),

        new(DriverKind.HidHide,
            "HidHide",
            "Nefarius.HidHide",
            "Hides the physical pad from games so input is not counted twice. Optional, but without it every press registers on two controllers.",
            "https://github.com/nefarius/HidHide/releases/latest",
            // HidHide is a HIDClass upper filter, and a filter driver only attaches when a
            // device stack is built — every stack that already existed keeps running without it.
            NeedsRebootAfterInstall: true)
    ];

    public static bool IsInstalled(DriverKind kind) => kind switch
    {
        DriverKind.ViGEmBus => RemapEngine.IsDriverInstalled(),
        DriverKind.HidHide => IsHidHideInstalled(),
        _ => false
    };

    private static bool IsHidHideInstalled()
    {
        try
        {
            // Constructed fresh each time: a cached service would still report the state
            // from before an install we just performed.
            return new HidHideControlService().IsInstalled;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<DriverRequirement> Missing() =>
        All.Where(d => !IsInstalled(d.Kind)).ToList();

    /// <summary>Where winget lives, or null when this machine has no winget at all.</summary>
    public static string? FindWinget()
    {
        var candidates = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe")
        };

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), "winget.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<InstallResult> InstallAsync(
        DriverRequirement driver,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var winget = FindWinget();
        if (winget is null)
        {
            return new InstallResult(false, -1,
                "winget is not available on this machine. Install the driver manually from " + driver.DownloadUrl);
        }

        var info = new ProcessStartInfo(winget)
        {
            // -e pins the exact id, and the explicit source avoids the Microsoft Store
            // mirror, whose certificate check fails on some machines.
            ArgumentList =
            {
                "install",
                "--id", driver.WingetId,
                "-e",
                "--source", "winget",
                "--accept-package-agreements",
                "--accept-source-agreements"
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var log = new StringBuilder();

        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            // winget redraws a progress bar on every tick; those frames are noise in a log.
            var clean = Clean(line);
            if (clean.Length == 0)
                return;

            lock (log)
                log.AppendLine(clean);

            progress?.Report(clean);
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new InstallResult(false, -1, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone; nothing to clean up.
            }

            return new InstallResult(false, -1, "Installation cancelled.");
        }

        // winget reports success before the driver's own installer has finished registering,
        // so trust the actual device probe over the exit code.
        var installed = IsInstalled(driver.Kind);
        string text;
        lock (log)
            text = log.ToString();

        return new InstallResult(installed || process.ExitCode == 0, process.ExitCode, text);
    }

    private static string Clean(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            // Spinner glyphs and box-drawing progress bars carry no information once logged.
            if (c is '█' or '░' or '▒' or '▓' or '\b' or '\r')
                continue;
            if (!char.IsControl(c) || c == '\t')
                builder.Append(c);
        }

        return builder.ToString().Trim();
    }
}
