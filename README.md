# Oriole

A clean-room Windows wallpaper manager. **Foundation build only — not yet installable.**

## Status

The `foundation/0.1.0` branch contains the initial source package and the first Windows integration slice.

- Native WPF interface with six sections.
- Wallhaven v1 API client and query builder with categories, purity, tags, colours, resolutions, sorting and pagination.
- Sequential scheduling engine designed to avoid overlapping changes and timer backlogs.
- Windows monitor discovery using the desktop wallpaper API.
- Local image selection and per-monitor wallpaper assignment.
- Initial unit tests, Windows CI workflow, build script, product spec and technical design.

Windows exposes one shared wallpaper fit mode. Independent fit modes per monitor still require an image-composition layer.

The source is available on [the foundation branch](https://github.com/DetectiveLon/oriole-wallpaper-manager/tree/foundation/0.1.0). Draft PR #1 is open for review and has not been merged.

## Not implemented yet

- Functional Wallhaven discovery browser and image previews
- Persistent local library, favourites and collections
- Per-display automation UI, tray controls and fullscreen deferral
- Installer and release packaging

Images fetched from Wallhaven remain subject to their original owners' rights.

## Repository layout

`main` retains the initial project-status README. Working source is developed on separate branches and proposed through pull requests.
