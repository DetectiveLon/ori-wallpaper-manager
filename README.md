# Ori

A clean-room, free and open-source wallpaper manager for Windows 11. Multi-monitor
is a first-class case. Development happens in reviewable branches and is proposed
through pull requests.

## What it does today

The `v2-shell` branch is the current working branch. It provides:

- Wallhaven discovery with categories, purity, tags, sorting, toplist, order,
  resolution, aspect ratio and colour filters, with pagination and previews.
- A local, versioned wallpaper library with search, favourites, collections,
  folder import, duplicate detection, apply and metadata-only removal.
- Independent per-display rotation schedules with countdown, manual rotate,
  pause/resume, stop and geometry-based full-screen pause.
- Automatic Wallhaven rotation from named rotation feeds with per-feed cadence
  and round-robin cycling, cached locally and applied automatically.
- A scaled monitor layout diagram, per-display image choice, and a live preview
  of all six Windows fit modes.
- A system-tray icon with Open/Exit and notifications, launch-at-sign-in, and a
  modern dark theme with a structured navigation rail.
- Settings with a DPAPI-protected Wallhaven API key, cache clearing and data
  folders.

See `docs/IMPLEMENTATION_STATUS.md` for the current, per-wave status and
`docs/UI-GUIDELINES.md` for the design rules behind the current interface.

## Known limitations

- Full-screen detection enumerates all visible top-level windows against each
  display's bounds (not just the foreground window), but it still does not
  identify games by process name. Live game verification is pending.
- Windows exposes one shared wallpaper fit mode through `IDesktopWallpaper`;
  independent per-monitor fit modes need an image-composition layer.
- The library renders all matching cards on one page (no virtualization).
- Binaries are not code-signed; Windows SmartScreen may warn on first run.
  Signing is deferred, not ignored.

## Install / run

Ori is shipped as a **self-contained, single-file win-x64** build: the .NET
runtime is bundled, so there is **no .NET Desktop Runtime to install**.

- **Portable:** download `ori-<version>-win-x64.zip` from
  [Releases](https://github.com/DetectiveLon/ori-wallpaper-manager/releases/latest),
  extract it and run `Ori.exe`.
- **Per-user installer (optional):** build the free Inno Setup installer from
  `tools/installer/` (see `tools/installer/README.md`). It installs to
  `%LOCALAPPDATA%\Programs\Ori`, adds Start menu and uninstall entries, and
  reuses the app's own launch-at-sign-in setting. User data under
  `%LOCALAPPDATA%\Ori` is never deleted on uninstall. Inno Setup is optional and
  not required for the portable release.

## Build from source

Requires the exact .NET 10 SDK pinned in `global.json` (10.0.404) and Windows
desktop targeting support.

```powershell
.\build.ps1              # restore + core tests + desktop build
.\build.ps1 -Publish     # delegates to scripts/publish.ps1 for a release package
```

`scripts/publish.ps1` is the single source of truth for release packaging. For a
tag-driven local build:

```powershell
pwsh ./scripts/publish.ps1 -Tag v1.0.0
```

It produces a self-contained single-file zip that includes `Ori.Updater.exe`, a
standalone EXE and a self-checked `update-manifest.json`, and prints their
SHA-256 hashes. See `docs/RELEASING.md` for the full release process: the
startup update check is wired; applying an update is still manual.

## Privacy

- No telemetry and no account requirement.
- The Wallhaven API key is stored on disk with **DPAPI scoped to the current
  Windows user** (`CurrentUser`) — it is encrypted and cannot be read by another
  user or machine. Discover no longer auto-saves it; save it explicitly under
  Settings.
- Application errors are written to `%LOCALAPPDATA%\Ori\logs\errors.log`.
  That log **rotates** (bounded size/history) and is **redacted** so API keys and
  other sensitive values are not written to disk in plaintext.
- Wallhaven images remain the property of their original owners.
