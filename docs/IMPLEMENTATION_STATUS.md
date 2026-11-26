# Implementation status

Current working branch: `v2-shell` (v1 work remains on `feature/library-automation`).

This is a progress record, not a release-readiness claim.

## Implemented (carried forward from v1)

- Wallhaven discovery with categories, purity, query expressions, sorting,
  toplist, order, resolution, aspect-ratio and colour filters, with pagination
  and thumbnail previews.
- Full-size preview, local save and apply to a selected display.
- Bounded original/thumbnail downloads and image decode limits.
- Versioned JSON library index under Local AppData; search, favourites, apply,
  open and metadata-only removal.
- Independent app-session rotation schedules per display with countdown, manual
  rotate, pause/resume, stop and serialized wallpaper apply.
- Versioned per-display schedule state under Local App Data; running schedules
  resume after an app restart with missed deadlines restarted fresh.
- Geometry-based full-screen pause for the foreground window.
- Settings with a DPAPI (`CurrentUser`) protected Wallhaven API key, cache
  clearing, data folders and version information. Discover loads the saved key
  but no longer auto-saves it.
- Dark-theme styles across buttons, combo boxes and other standard controls.

## Implemented in v2-shell

- XAML views + view models for all screens, reusing the unchanged services and
  Core libraries.
- Library list with lazy thumbnail decode, collections, folder import and
  SHA-256 duplicate detection.
- System-tray icon with Open/Exit, launch-at-sign-in (HKCU Run) and balloon
  notifications (`TrayIconService.Notify`).
- Automatic Wallhaven rotation from an ordered list of named feeds
  (`WallhavenFeedStore` v2) with per-feed cadence, round-robin cycling,
  persisted backoff, local caching and silent v1 migration; tag presets and an
  editable tag/collection ComboBox. Feeds are authored in Discover and managed
  on Auto rotation.
- App-wide dark implicit styles and a custom window chrome.
- Static regression guards: `scripts/check-theme.ps1`, `check-buttons.ps1`,
  `check-tray.ps1`, `check-invariants.ps1`.
- **Packaging (this pass):** self-contained single-file win-x64 release via
  `scripts/publish.ps1`; optional per-user Inno Setup installer under
  `tools/installer/`; separate `tools/Ori.Updater` process; exact SDK pin in
  `global.json`; tag-driven single version source in `Ori.Desktop.csproj` +
  `app.manifest`; pre-publish verification in `release.yml`.

## Not implemented / outstanding

- Identifying games by process/executable name or detecting background
  full-screen windows.
- Independent per-monitor wallpaper fit modes.
- Library virtualization (all matching cards on one page are rendered).
- **Auto-update wiring:** `Ori.Updater` is built and the manifest is
  published, but the version-check call in `App.xaml.cs` (another agent's file)
  is not added. See `docs/RELEASING.md` for the exact one-line integration.
- Rename of library entries; a learned tag-preference model.
- Code signing / SmartScreen reputation (deferred, tracked in
  `docs/RELEASING.md`).

## Error reporting / privacy notes

- `%LOCALAPPDATA%\Ori\logs\errors.log` records unhandled errors. It rotates
  (bounded size/history) and is redacted so keys and sensitive values are not
  written in plaintext; the path is shown under Settings → DATA & ABOUT.
- The Wallhaven API key is stored on disk encrypted with DPAPI `CurrentUser`.

## Validation

CI (`.github/workflows/ci.yml`) runs Core tests on Linux and a full-solution
build plus the static guards on Windows on pushes to `main`, `v2-shell` and
`foundation/**`/`feature/**`/`work/**` branches and on all PRs. The Release
workflow runs the same checks before publishing. This confirms compilation and
automated tests, but does not replace an interactive check on a real
multi-monitor Windows desktop.
