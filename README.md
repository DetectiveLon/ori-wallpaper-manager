# Oriole

A clean-room Windows 11 wallpaper manager. Working source is developed in reviewable branches and proposed through pull requests. This is not yet a release build or installer.

## Current implementation

The `foundation/0.1.0` branch provides the WPF shell, Wallhaven API client and query builder, Windows monitor detection, per-monitor wallpaper assignment, a sequential rotation engine, initial tests, and build scaffolding.

The `feature/library-automation` branch builds on that foundation and preserves the existing Wallhaven discovery page and image downloader. It adds:

- Wallhaven discovery filters for categories, purity, tags, sorting, toplist period, order, custom minimum/exact resolution, aspect ratio, and color palette.
- Search pagination, thumbnail previews, full-image preview, save-to-library, and selected-display apply.
- Bounded image download and decode limits.
- A versioned local library index, offline saved images, search, favourites, apply, open-file, and metadata-only removal.
- Independent app-session rotation schedules for each connected display, each with its own interval, countdown, manual rotate, pause/resume, and stop controls.
- Optional per-display pause while a full-screen foreground window covers that screen. Detection is based on window geometry, so it applies to full-screen apps generally rather than identifying games by process name.

## Known gaps

- Rotation schedules run only during the current app session and are not restored after Oriole restarts.
- Full-screen detection uses the foreground window's bounds. It does not identify game executables or detect background full-screen windows.
- Library collections, local-folder import, duplicate detection, tray controls, and release packaging are not implemented.
- Windows exposes one shared wallpaper fit mode through `IDesktopWallpaper`; independent per-monitor fit modes need an image-composition layer.
- Latest CI for the current feature branch has passed Core tests and the Windows desktop build. A user-side interactive check on a multi-monitor Windows desktop is still needed before treating the new automation UI as release-ready.

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
