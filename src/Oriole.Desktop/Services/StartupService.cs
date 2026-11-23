using Microsoft.Win32;

namespace Oriole.Desktop.Services;

/// <summary>
/// Manages Oriole's per-user launch-at-login entry in the HKCU Run key.
/// No elevation is required: the value is written under the current user.
/// Operations never throw; failures are reported through <see cref="LastError"/>.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Oriole";

    /// <summary>Empty after a successful operation, otherwise the failure message.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Returns true when a non-empty "Oriole" value is present in the HKCU
    /// Run key. Returns false (and sets <see cref="LastError"/>) on read failure.
    /// </summary>
    public bool IsEnabled()
    {
        LastError = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null)
                return false;

            return key.GetValue(ValueName) is string text && !string.IsNullOrWhiteSpace(text);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the quoted executable path from the HKCU Run key.
    /// Failures are reported through <see cref="LastError"/> and never thrown.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        LastError = string.Empty;
        try
        {
            if (!enabled)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                LastError = "Cannot determine the executable path for startup registration.";
                return;
            }

            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (runKey is null)
            {
                LastError = "Unable to open the HKCU Run key for writing.";
                return;
            }

            runKey.SetValue(ValueName, "\"" + path + "\"", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Flips the current state. A failed read does not attempt a write.</summary>
    public void Toggle()
    {
        var enabled = IsEnabled();
        if (LastError.Length != 0)
            return;

        SetEnabled(!enabled);
    }
}
