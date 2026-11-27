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
- Geometry-based full-screen pause: all visible top-level windows are enumerated
  and matched against each display's bounds, not just the foreground window;
  known full-screen windows are remembered through brief focus changes.
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
- App-wide dark implicit styles and a custom window chrome; title-bar
  double-click is handled through `MouseLeftButtonDown` + `ClickCount == 2`
  (`Border` has no `MouseDoubleClick` event). The orphaned `OverviewView` files
  are gone; Discover is the only landing page.
- Start-minimized preference (`UiPreferencesStore`, default true) under
  `%LOCALAPPDATA%\Ori\ui-preferences.json`, editable in Settings.
- Auto-update **check** is wired: `App.OnStartup` launches `UpdateChecker`, which
  runs the bundled `Ori.Updater.exe check` in the background and reports status
  in Settings plus a tray balloon. Applying an update is still a deliberate
  manual download/install, not an automatic in-app swap.
- Static regression guards: `scripts/check-theme.ps1`, `check-buttons.ps1`,
  `check-tray.ps1`, `check-invariants.ps1`. `check-buttons.ps1` now allowlists the
  documented binding-only controls and runs with `-Strict` in CI/release;
  `check-invariants.ps1` also checks handler bodies and hardcoded app versions.
- **Packaging (this pass):** self-contained single-file win-x64 release via
  `scripts/publish.ps1`; optional per-user Inno Setup installer under
  `tools/installer/`; separate `tools/Ori.Updater` process; exact SDK pin in
  `global.json`; tag-driven single version source in `Ori.Desktop.csproj` +
  `app.manifest`; pre-publish verification in `release.yml`. The zip also
  stages `Ori.Updater.exe`, and `publish.ps1` parses its own manifest back to
  verify version/asset alignment. The optional installer installs binaries to
  `%LOCALAPPDATA%\Programs\Ori` and never deletes user data under
  `%LOCALAPPDATA%\Ori`.

## Not implemented / outstanding

- Identifying games by process/executable name or detecting background
  full-screen windows.
- Independent per-monitor wallpaper fit modes.
- Library virtualization (all matching cards on one page are rendered).
- **Auto-update apply:** the startup check and status reporting are wired; the
  in-app "apply now" swap is not. Updates still require a manual download from
  GitHub Releases. See `docs/RELEASING.md`.
- Rename of library entries; a learned tag-preference model.
- Code signing / SmartScreen reputation (deferred, tracked in
  `docs/RELEASING.md`).

## Error reporting / privacy notes

- `%LOCALAPPDATA%\Ori\logs\errors.log` records unhandled errors. It rotates
  (bounded size/history) and is redacted so keys and sensitive values are not
  written in plaintext; the path is shown under Settings → DATA & ABOUT.
- The Wallhaven API key is stored on disk encrypted with DPAPI `CurrentUser`.

## Validation

CI (`.github/workflows/ci.yml`) runs Core tests on Linux **and on Windows** (the
Windows job covers the case-insensitive path-comparison branch), a full-solution
build, the new `Ori.Desktop.Tests` helper suite and the static guards on pushes
to `main`, `v2-shell` and `foundation/**`/`feature/**`/`work/**` branches and on
all PRs. The Release workflow runs the same checks before publishing. This
confirms compilation and automated tests, but does not replace an interactive
check on a real multi-monitor Windows desktop.

### Pending verification in this wave

The new desktop test project, the publish/updater packaging change and the
guard updates were authored but **not built or run locally** in this agent pass
(the repository's agent rules reserve builds for the integrator). CI is the
first place they execute as a whole. Static checks that were run:
`check-buttons.ps1` (21 documented binding-only controls allowlisted, 0
remaining findings, also 0 under `-Strict`) and `check-invariants.ps1` (including the new handler-body and version guards;
both scripts now pass on the current tree).
