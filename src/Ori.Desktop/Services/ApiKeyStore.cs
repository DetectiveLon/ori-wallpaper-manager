using System.Security.Cryptography;
using System.Text;

namespace Ori.Desktop.Services;

/// <summary>
/// Wallhaven API key storage. The key is protected with DPAPI under the current
/// Windows user, so it is not stored as plaintext in the app data folder.
/// </summary>
public static class ApiKeyStore
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "settings", "wallhaven.key");

    public static bool HasKey
    {
        get
        {
            var found = TryLoad(out var key, out _);
            return found && !string.IsNullOrWhiteSpace(key);
        }
    }

    /// <summary>
    /// Loads the key and distinguishes "nothing stored" from "stored but unreadable".
    /// DPAPI can fail after a Windows profile or password change; silently returning
    /// an empty key in that case makes Settings claim the user never had one.
    /// </summary>
    public static bool TryLoad(out string key, out string? error)
    {
        if (!File.Exists(KeyPath))
        {
            key = string.Empty;
            error = null;
            return true;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(KeyPath);
            key = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            key = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    public static string Load()
    {
        return TryLoad(out var key, out _) ? key : string.Empty;
    }

    public static void Save(string key)
    {
        var value = key?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            Clear();
            return;
        }

        var directory = Path.GetDirectoryName(KeyPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        var temporary = KeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, KeyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>Removes the stored key. Returns false when the file could not be
    /// deleted (locked, ACL, ...), so callers never report a false success.</summary>
    public static bool Clear()
    {
        try
        {
            if (!File.Exists(KeyPath))
                return true;
            File.Delete(KeyPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
