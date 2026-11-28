# Ori QA findings — feature/library-automation @ 34b2dba

> **Historical / scoped document.** These findings were captured on the older
> `feature/library-automation` branch during an earlier hardening session. They
> are preserved for context only and are **not** the status of the current
> `v2-shell` branch. For current status see `docs/IMPLEMENTATION_STATUS.md`,
> `docs/BUG-INVENTORY.md` and `docs/V2-ARCHITECTURE.md`. No user-specific paths
> are recorded here.

Live-tested on a Windows 11 desktop with SDK 10.0.404, Release build from
`build.ps1`. Screenshot filenames are listed without any local file paths.

## Current `v2-shell` cross-check (not live-verified in this wave)

The older items below are kept verbatim for traceability. On `v2-shell` their
status is now:

- Full-screen pause is **no longer foreground-only**:
  `FullscreenWindowDetector` enumerates all visible top-level windows and matches
  each display's geometry, remembering known blockers through focus changes.
  A real game must still be smoke-tested before this is closed as live-verified.
- The title-bar double-click handler is **wired**: the title bar `Border` has no
  `MouseDoubleClick` event, so `MouseLeftButtonDown` dispatches to
  `OnTitleBarMouseDoubleClick` when `ClickCount == 2`.
- `OverviewView` is **gone** (files deleted, nav key removed); Discover is the
  landing page.
- Auto-update is **being wired**: `App.OnStartup` now calls `UpdateChecker`,
  which runs the bundled `Ori.Updater.exe check` in the background and reports
  status in Settings plus a tray balloon. Applying an update is still manual;
  the network path has not been live-tested.
- The optional installer puts binaries in `%LOCALAPPDATA%\Programs\Ori`, while
  all user data stays in `%LOCALAPPDATA%\Ori` and is preserved on uninstall.
- New `tests/Ori.Desktop.Tests` unit tests cover trusted URIs, wallpaper path
  validation, log redaction, start-minimized persistence and manifest alignment.
  They are authored here but their first execution is CI (no local build in this
  wave).

## Final status (that session)

- Fixed 14 issues; 9 new regression tests. 45/45 core tests pass, WPF build 0 warnings/0 errors.
- Final smoke: app launches, both monitors detected, Displays and Automation render, no modal; a seeded schedule restored with a live countdown; graceful close now exits cleanly. Live close testing found and fixed a WPF re-entrant `Close()` bug in the first shutdown implementation.
- Fixed in addition to item 1 below: silent process exit (global exception handlers + wrapped library handlers); Library UI decoded every image on the UI thread and re-rendered per keystroke (now background decode + 180 ms debounce); Start error overwritten by cleanup; wallpaper apply forcing global Fill fit mode; Discover Search button off-screen; blocking detection modal; reset-during-pause double notification; throwing loop callbacks killing rotation; API key forwarded on auto-redirecting HttpClient; no path back to "Unsorted"; relative index path crash; invalid WallhavenOrder silently mapping to "desc"; stale Overview copy; CTA highlighted as nav.
- Still open at that time: duplicated detection presentation in three pages (detection itself now shares one service); two disagreeing image pipelines; foreground-only full-screen detection (now all-window enumeration in `v2-shell`); schedule persistence not surviving a monitor-ID change; library no virtualization or collections UI. Several of these were addressed later in `v2-shell`.
- Not tested then: real Wallhaven search/download, full-screen pause with a game, installer.

- UI pass: Settings was a dead placeholder — now has a DPAPI-protected Wallhaven API key field, cache clearing, data paths, and version info; key save/remove verified live. Default WPF buttons/comboboxes rendered as white blocks (disabled state overrode local colours); added dark implicit styles and verified the interval dropdown still opens/selects and disabled buttons stay dark. Docs updated.

## Fixed during that session

### 1. High — one stale display path made *all* display detection fail
- Where: `src/Ori.Desktop/Services/WindowsDesktopWallpaper.cs:46-56` (original).
- Repro: open Discover/Displays/Automation; banner "Display detection unavailable: Unexpected HRESULT has been returned from a call to a COM component."; Displays opened a modal "Display detection failed".
- Root cause (proved with an STA probe against the compiled DLL): `IDesktopWallpaper.GetMonitorDevicePathCount` returned extra stale duplicate instance paths; `GetMonitorRECT` throws `COMException 0x80004005` on those. The loop did not catch it, so the whole enumeration aborted and the app reported zero displays.
- Fix: catch `COMException` per monitor path and skip it; also tolerate a failed `GetWallpaper` read. Healthy monitors are still returned with geometry, primary flag and wallpaper.
- Verified: probe returned the two real monitors; UI Displays page listed both cards; Automation rendered two independent schedule panels. 36/36 tests passed, build 0 warnings/0 errors.

## Initially open findings (superseded by the final status above where noted)

### 2. Medium — blocking modal for a recoverable detection failure — FIXED
- Where: `src/Ori.Desktop/MainWindow.xaml.cs:149-153` (original).
- `LoadDisplays` showed `MessageBox.Show` on any exception, freezing the page behind a dialog. Expected: inline status text plus a Refresh action, never a modal.

### 3. Medium — duplicated display detection, three different error presentations
- Detection and error handling were duplicated across the shell and two pages. Expected: a single shared snapshot/refresh service later implemented in `v2-shell`.

### 4. Medium — Discover filter column overflowed the window
- At the app's default size the Search button sat below the visible window. Expected: responsive layout with Search always visible.

### 5. Low — Settings was a dead end
- Page showed "will live here" placeholders; no controls existed. Later branches added the DPAPI key field, cache clearing and data paths.

### 6. Low — stale Overview copy
- Bottom banner described features as "being added" although they were implemented on that branch.

### 7. Low — empty-library actions were enabled
- With 0 saved wallpapers, Start and Rotate remained enabled and only reported an error after clicking. Expected: disabled with the reason visible.

### 8. Unconfirmed — app process/window state changed between checks
- A process was alive and responding, then gone on the next check with no event-log error; another appeared minimized without action. No crash evidence, so not classified as a defect. Needs a reproducible observation.

## Not tested (on that branch)
- Real Wallhaven search/download (no API key configured).
- Full-screen pause with an actual game.
- Schedule persistence across restarts.
- Installer/release package: none existed at that time. `v2-shell` now produces a self-contained portable zip; the optional installer and updater are documented in `docs/RELEASING.md` and `tools/installer/README.md`.
