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
/// The stored id is validated against the monitors Ori can currently see; a
/// stale id (unplugged monitor, driver re-enumeration) simply falls back to the
/// primary display instead of making Apply fail.
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

    /// <summary>The remembered monitor id, or null when none has been chosen.</summary>
    public string? SelectedMonitorId { get; private set; }

    /// <summary>
    /// Loads the remembered id, discarding it when it no longer matches a live
    /// monitor. Call this once the current monitor list is known.
    /// </summary>
    public void Load(IEnumerable<string> liveMonitorIds)
    {
        // Clear first: an unplugged monitor must not leave the previous in-memory
        // id behind when the file is missing, stale or unreadable.
        SelectedMonitorId = null;

        try
        {
            if (!File.Exists(_selectionPath))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(_selectionPath));
            if (!document.RootElement.TryGetProperty("monitorId", out var element))
                return;

            var id = element.GetString();
            if (!string.IsNullOrWhiteSpace(id)
                && liveMonitorIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                SelectedMonitorId = id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged selection is not worth surfacing; fall back.
            SelectedMonitorId = null;
        }
    }

    /// <summary>Remembers a choice and persists it for the next page or session.</summary>
    public void Select(string? monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
            return;

        SelectedMonitorId = monitorId;
        try
        {
            var directory = Path.GetDirectoryName(_selectionPath);
            if (directory is not null)
                Directory.CreateDirectory(directory);
            File.WriteAllText(_selectionPath, JsonSerializer.Serialize(new { monitorId }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In-memory choice still works for this session.
        }
    }

    /// <summary>Returns the remembered monitor, or the caller's primary fallback.</summary>
    public string? Resolve(string? primaryMonitorId) =>
        string.IsNullOrWhiteSpace(SelectedMonitorId) ? primaryMonitorId : SelectedMonitorId;
}
