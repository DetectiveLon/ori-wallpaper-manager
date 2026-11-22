# Oriole

A clean-room Windows 11 wallpaper manager. Source is being developed in reviewable feature branches. It is not yet a release build or installer.

## Current implementation

The `foundation/0.1.0` branch provides the WPF shell, Wallhaven API client/query builder, Windows monitor detection, per-monitor wallpaper assignment, a sequential rotation engine, initial tests, and build scaffolding.

The `feature/discover` branch builds on that foundation and currently adds:

- Wallhaven discovery with independent General / Anime / People categories and SFW / Sketchy / NSFW purity filters.
- Search expressions, sorting, toplist periods, resolution, aspect-ratio, and multi-select colour filters.
- Result pagination, thumbnails, full-image preview, download validation, and per-monitor apply.
- A local wallpaper library with a versioned JSON index, search, favourites, and safe metadata-only removal.
- App-session wallpaper rotation from the local library with a live countdown, manual rotate, pause/resume, and stop.

## Known gaps

- Automation currently runs for one selected display during the current app session. Per-display schedules and restart persistence are not implemented.
- Fullscreen-game detection, tray integration, startup registration, collections, and release packaging are not implemented.
- Windows exposes one shared wallpaper fit mode through `IDesktopWallpaper`; independent per-monitor fit modes need an image-composition layer.
- The first Windows CI runs have failed before a runner was assigned. No successful compile/test result is available yet.

## Build

Requires the .NET 10 SDK and Windows desktop targeting support.

```powershell
.\build.ps1
```

To publish a self-contained win-x64 folder:

```powershell
.\build.ps1 -Publish
```

Wallhaven images remain the property of their original owners. The API key is only kept in memory by the current discovery page; persistent encrypted key storage is not implemented yet.
