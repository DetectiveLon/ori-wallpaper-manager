using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ori.Updater;

/// <summary>
/// Small helper that checks a JSON update manifest published as a GitHub
/// Release asset, verifies the SHA-256 of the new single-file EXE, and — in a
/// SEPARATE process from the main app — waits for Ori to exit, swaps the
/// executable, and optionally relaunches it. The main app cannot overwrite its
/// own running EXE, which is why this lives in its own project.
/// </summary>
internal static class Program
{
    private const string DefaultManifestUrl =
        "https://github.com/DetectiveLon/ori-wallpaper-manager/releases/latest/download/update-manifest.json";

    // Bounded download so a hostile/broken manifest cannot fill the disk.
    private const long MaxDownloadBytes = 200L * 1024 * 1024;

    // Redirects are followed manually by SecureHttp so every hop is scheme-checked.
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "Ori.Updater/1.0" } }
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                PrintUsage();
                return 0;
            }

            var targetExe = Path.GetFullPath(options.ExePath);
            var manifest = await LoadManifestAsync(options.ManifestUrl).ConfigureAwait(false);

            if (options.Action == UpdateAction.Check)
            {
                var current = ReadInformationalVersion(targetExe);
                var updateAvailable = IsNewer(manifest.Version, current);
                Console.WriteLine(JsonSerializer.Serialize(new CheckResult(
                    updateAvailable,
                    current,
                    manifest.Version,
                    manifest.AssetName,
                    manifest.Sha256,
                    manifest.Url)));
                return 0;
            }

            // Apply: download + verify BEFORE touching the running app.
            var tempDir = Path.Combine(Path.GetTempPath(), "ori-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var downloadPath = Path.Combine(tempDir, manifest.AssetName);
                await DownloadAndVerifyAsync(manifest, downloadPath).ConfigureAwait(false);

                if (options.ParentPid > 0)
                    await WaitForExitAsync(options.ParentPid, options.WaitSeconds).ConfigureAwait(false);

                var backupPath = targetExe + ".bak";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                File.Move(targetExe, backupPath);
                try
                {
                    File.Move(downloadPath, targetExe);
                }
                catch
                {
                    if (File.Exists(backupPath))
                        File.Move(backupPath, targetExe);
                    throw;
                }

                // The swap succeeded: the .bak is no longer needed. Cleanup is
                // best effort (a stale file is also removed by the installer's
                // UninstallDelete entry), so a locked backup never fails an update.
                try
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (options.Relaunch && File.Exists(targetExe))
                {
                    Process.Start(new ProcessStartInfo(targetExe) { UseShellExecute = true });
                }

                Console.WriteLine($"Updated Ori to {manifest.Version}.");
                return 0;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch (IOException) { }
            }
        }
        catch (UpdateException ex)
        {
            Console.Error.WriteLine("Update failed: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Update failed: " + ex.Message);
            return 1;
        }
    }

    private static async Task<UpdateManifest> LoadManifestAsync(string url)
    {
        SecureHttp.RequireHttps(url, "The update manifest URL must be HTTPS.");

        using var response = await SecureHttp.GetAsync(Client, url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await UpdateManifestParser.ParseAsync(stream).ConfigureAwait(false);
    }

    private static async Task DownloadAndVerifyAsync(UpdateManifest manifest, string destination)
    {
        using var response = await SecureHttp.GetAsync(Client, manifest.Url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > MaxDownloadBytes)
            throw new UpdateException("The update asset is larger than the allowed download limit.");

        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new UpdateException("The update asset exceeded the allowed download limit.");
            await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        await target.FlushAsync().ConfigureAwait(false);

        string actualHash;
        await using (var hashStream = File.OpenRead(destination))
        {
            var hash = await SHA256.HashDataAsync(hashStream).ConfigureAwait(false);
            actualHash = Convert.ToHexString(hash);
        }

        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("SHA-256 mismatch: the downloaded file does not match the signed manifest.");
        }
    }

    private static string ReadInformationalVersion(string exePath)
    {
        if (!File.Exists(exePath))
            return "0.0.0";

        var info = FileVersionInfo.GetVersionInfo(exePath);
        return NormalizeVersion(info.ProductVersion);
    }

    private static bool IsNewer(string latest, string current)
    {
        return TryParseVersion(latest, out var latestVersion) &&
               TryParseVersion(current, out var currentVersion) &&
               latestVersion > currentVersion;
    }

    private static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "0.0.0";
        var plus = value.IndexOf('+');
        var trimmed = plus >= 0 ? value[..plus] : value;
        return string.IsNullOrWhiteSpace(trimmed) ? "0.0.0" : trimmed.Trim();
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        if (!Version.TryParse(NormalizeVersion(value), out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    private static async Task WaitForExitAsync(int pid, int waitSeconds)
    {
        Process? process = null;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // already gone
        catch (InvalidOperationException) { return; }

        if (process is null)
            return;

        using (process)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 1, 600));
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited)
                    return;
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Ori.Updater - check/apply a GitHub Releases update.");
        Console.WriteLine("Usage:");
        Console.WriteLine("  Ori.Updater check [--exe <path>] [--manifest-url <url>]");
        Console.WriteLine("  Ori.Updater apply --exe <path> [--pid <n>] [--wait-seconds <n>]");
        Console.WriteLine("                       [--relaunch] [--manifest-url <url>]");
        Console.WriteLine($"Default manifest: {DefaultManifestUrl}");
    }

    private sealed record CheckResult(
        bool UpdateAvailable,
        string CurrentVersion,
        string LatestVersion,
        string AssetName,
        string Sha256,
        string Url);

    private enum UpdateAction { Check, Apply }

    private sealed class Options
    {
        public UpdateAction Action { get; private init; }
        public string ExePath { get; private init; } = "Ori.exe";
        public string ManifestUrl { get; private init; } = DefaultManifestUrl;
        public int ParentPid { get; private init; }
        public int WaitSeconds { get; private init; } = 120;
        public bool Relaunch { get; private init; }
        public bool Help { get; private init; }

        public static Options Parse(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return new Options { Help = true };

            var action = args[0] switch
            {
                "check" => UpdateAction.Check,
                "apply" => UpdateAction.Apply,
                _ => throw new UpdateException($"Unknown action '{args[0]}'. Use 'check' or 'apply'.")
            };

            string exe = "Ori.exe";
            string manifestUrl = DefaultManifestUrl;
            int pid = 0;
            int waitSeconds = 120;
            bool relaunch = false;

            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--exe" when i + 1 < args.Length:
                        exe = args[++i];
                        break;
                    case "--manifest-url" when i + 1 < args.Length:
                        manifestUrl = args[++i];
                        break;
                    case "--pid" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                            throw new UpdateException("--pid must be an integer.");
                        break;
                    case "--wait-seconds" when i + 1 < args.Length:
                        if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out waitSeconds))
                            throw new UpdateException("--wait-seconds must be an integer.");
                        break;
                    case "--relaunch":
                        relaunch = true;
                        break;
                    default:
                        throw new UpdateException($"Unknown or incomplete argument '{args[i]}'.");
                }
            }

            return new Options
            {
                Action = action,
                ExePath = exe,
                ManifestUrl = manifestUrl,
                ParentPid = pid,
                WaitSeconds = waitSeconds,
                Relaunch = relaunch
            };
        }
    }
}
