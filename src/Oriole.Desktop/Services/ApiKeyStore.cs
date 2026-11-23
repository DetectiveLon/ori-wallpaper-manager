using System.Security.Cryptography;
using System.Text;

namespace Oriole.Desktop.Services;

/// <summary>
/// Wallhaven API key storage. The key is protected with DPAPI under the current
/// Windows user, so it is not stored as plaintext in the app data folder.
/// </summary>
public static class ApiKeyStore
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Oriole", "settings", "wallhaven.key");

    public static bool HasKey => !string.IsNullOrWhiteSpace(Load());

    public static string Load()
    {
        try
        {
            if (!File.Exists(KeyPath))
                return string.Empty;
            var protectedBytes = File.ReadAllBytes(KeyPath);
            var bytes = ProtectedData.Unprotect(
                protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            // A key written by another Windows user or a damaged file is treated as absent.
            return string.Empty;
        }
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
