using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ori.Desktop.Services;

/// <summary>
/// Remembers which display the "Apply to" picker defaults to. Without this,
/// visiting Library or Discover silently reset the target to the primary
/// monitor, so a wallpaper could land on the wrong screen.
///
/// The stored key is the panel's stable fingerprint, never its volatile COM
/// device path. Callers pass fingerprints to <see cref="Load"/>,
/// <see cref="Select"/> and <see cref="Resolve"/>. A fingerprint that no longer
/// matches a live panel (or an old COM-path value left by an earlier build)
/// falls back to the primary display instead of making Apply fail.
/// </summary>
public sealed class MonitorSelectionService
{
    private static readonly string SelectionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ori", "settings", "selected-monitor.json");

    private readonly string _selectionPath;

    public static MonitorSelectionService Instance { get; } = new();

    public MonitorSelectionService()
        : this(SelectionPath)
    {
    }

    /// <summary>Test hook: a redirected selection file, same format as production.</summary>
    internal MonitorSelectionService(string selectionPath) => _selectionPath = selectionPath;

    /// <summary>The remembered fingerprint, or null when none has been chosen.</summary>
    public string? SelectedMonitorId { get; private set; }

    /// <summary>Fingerprint alias for callers that think in panel identity.</summary>
    public string? SelectedFingerprint => SelectedMonitorId;

    /// <summary>
    /// Loads the remembered fingerprint, discarding it when it no longer matches
    /// a live panel. Call this once the current monitor list is known.
    /// </summary>
    public void Load(IEnumerable<string> liveMonitorFingerprints)
    {
        // Clear first: an unplugged monitor must not leave the previous in-memory
        // key behind when the file is missing, stale or unreadable.
        SelectedMonitorId = null;

        try
        {
            if (!File.Exists(_selectionPath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(_selectionPath));
            var root = document.RootElement;
            // "fingerprint" is the current key; "monitorId" is kept for files
            // written by older builds, whose value is validated the same way.
            var found = root.TryGetProperty("fingerprint", out var element)
                || root.TryGetProperty("monitorId", out element);
            if (!found)
                return;

            var key = element.GetString();
            if (!string.IsNullOrWhiteSpace(key)
                && liveMonitorFingerprints.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                SelectedMonitorId = key;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged selection is not worth surfacing; fall back.
            SelectedMonitorId = null;
        }
    }

    /// <summary>Remembers a fingerprint choice and persists it for the next page or session.</summary>
    public void Select(string? monitorFingerprint)
    {
        if (string.IsNullOrWhiteSpace(monitorFingerprint))
            return;

        SelectedMonitorId = monitorFingerprint;
        try
        {
            var directory = Path.GetDirectoryName(_selectionPath);
            if (directory is not null)
                Directory.CreateDirectory(directory);
            // Both names carry the same fingerprint: "monitorId" keeps older
            // readers working, "fingerprint" names what is actually stored.
            File.WriteAllText(_selectionPath, JsonSerializer.Serialize(
                new { monitorId = monitorFingerprint, fingerprint = monitorFingerprint }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In-memory choice still works for this session.
        }
    }

    /// <summary>Returns the remembered fingerprint, or the caller's primary fallback.</summary>
    public string? Resolve(string? primaryMonitorFingerprint) =>
        string.IsNullOrWhiteSpace(SelectedMonitorId) ? primaryMonitorFingerprint : SelectedMonitorId;
}
