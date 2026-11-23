# Oriole

A clean-room Windows 11 wallpaper manager. Working source is developed in reviewable branches and proposed through pull requests. This is not yet a release build or installer.

## Current implementation

The `foundation/0.1.0` branch provides the WPF shell, Wallhaven API client and query builder, Windows monitor detection, per-monitor wallpaper assignment, a sequential rotation engine, initial tests, and build scaffolding.

The `feature/library-automation` branch builds on that foundation and preserves the existing Wallhaven discovery page and image downloader. It adds:

- Wallhaven discovery filters for categories, purity, tags, sorting (including Hot), toplist period, order, custom minimum/exact resolution, aspect ratio, and color palette.
- Search pagination, thumbnail previews, full-image preview, save-to-library, and selected-display apply.
- Bounded image download and decode limits.
- A versioned local library index, offline saved images, search, favourites, apply, open-file, and metadata-only removal.
- App-session wallpaper rotation from the local library with a visible countdown, manual rotate, pause/resume, and stop.

## Known gaps

- Rotation currently targets one selected display and runs only during the current app session.
- Fullscreen-game detection, per-display schedules, schedule persistence across restarts, and automatic startup are not implemented.
- Library collections, local-folder import, duplicate detection, tray controls, and release packaging are not implemented.
- Windows exposes one shared wallpaper fit mode through `IDesktopWallpaper`; independent per-monitor fit modes need an image-composition layer.
- Successful Windows compile and test results are still required before merging.

## Build

Requires the .NET 10 SDK and Windows desktop targeting support.

```powershell
.\build.ps1
```

To publish a self-contained win-x64 folder:

```powershell
.\build.ps1 -Publish
```

Wallhaven images remain the property of their original owners. The API key is currently kept in memory by the Discover page and is not saved to disk.
