using System.Diagnostics;
using System.Text.Json;

namespace Ori.Desktop.Services;

/// <summary>
/// Optional startup update check. Ori cannot replace its own running EXE, so this
/// only launches the bundled Ori.Updater helper in "check" mode and reports the
/// result. Applying an update stays a deliberate user action; nothing here needs
/// credentials or blocks the UI. A missing helper (development builds) is a no-op.
/// </summary>
public static class UpdateChecker
{
    private static readonly object Gate = new();
    private static string _statusText = string.Empty;
    private static bool _started;

    /// <summary>Short, user-facing status for the Settings page. Never throws.</summary>
    public static string LastStatusText
    {
        get { lock (Gate) return _statusText; }
    }

    /// <summary>
    /// Raised once when the background check finishes, whether or not it found an
    /// update. Views that are already open can refresh from <see cref="LastStatusText"/>.
    /// The callback runs on a worker thread, so subscribers must marshal to the UI
    /// dispatcher before touching controls or view models.
    /// </summary>
    public static event Action? Completed;

    /// <summary>
    /// Starts a bounded, background version check once per process. Callers must
    /// not await this: startup continues whether the check succeeds or not.
    /// </summary>
    public static Task CheckOnStartupAsync()
    {
        lock (Gate)
        {
            if (_started)
                return Task.CompletedTask;
            _started = true;
            _statusText = string.Empty;
        }

        return Task.Run(CheckCoreAsync);
    }

    private static async Task CheckCoreAsync()
    {
        try
        {
            var appPath = Environment.ProcessPath;
            var directory = string.IsNullOrWhiteSpace(appPath)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory;
            var updater = Path.Combine(directory, "Ori.Updater.exe");
            if (!File.Exists(updater))
                return; // Optional component: dev builds and portable runs may omit it.

            var start = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("check");
            start.ArgumentList.Add("--exe");
            start.ArgumentList.Add(appPath ?? "Ori.exe");

            using var process = Process.Start(start);
            if (process is null)
                return;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { /* already gone */ }
                return;
            }

            if (process.ExitCode != 0)
                return; // A broken/offline check is simply not reported.

            var json = await stdout.ConfigureAwait(false);
            _ = stderr; // Observed so the error pipe cannot fill while we wait.
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024)
                return;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!GetBool(root, "updateAvailable"))
            {
                SetStatus($"Ori is up to date ({GetString(root, "currentVersion")}).");
                return;
            }

            var current = GetString(root, "currentVersion");
            var latest = GetString(root, "latestVersion");
            SetStatus($"Update available: {latest} (installed: {current}). Download it from the GitHub Releases page.");

            try
            {
                App.Tray?.Notify(
                    "Ori update available",
                    $"Version {latest} is available (you have {current}). See Settings for details.");
            }
            catch (Exception)
            {
                // A failed balloon must never turn a version check into a crash.
            }
        }
        catch (Exception)
        {
            // Update checking is optional: network, JSON and process failures are
            // deliberately silent. The app itself stays fully usable.
        }
        finally
        {
            // A check that finishes before Settings is opened still needs to
            // notify a Settings page the user already has open.
            try { Completed?.Invoke(); }
            catch (Exception) { /* A bad subscriber cannot break the checker. */ }
        }
    }

    private static void SetStatus(string text)
    {
        lock (Gate)
            _statusText = text;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.True)
                return true;
        }
        return false;
    }

    private static string GetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
