# Oriole

A clean-room Windows wallpaper manager. **Foundation / early preview — not yet installable.**

## Status

The `foundation/0.1.0` branch contains the initial source package and two usable Windows feature slices.

- Native WPF interface with six sections.
- Wallhaven v1 API client and query builder.
- Discovery page with categories, content levels, tag operators, sorting/toplist period, order, minimum or exact resolution, aspect-ratio presets, colour filters and pagination.
- Bounded thumbnail and full-image downloads restricted to Wallhaven HTTPS hosts.
- Image preview cards, browser links, and direct apply-to-display action. Applied downloads are saved under the user's local Oriole wallpaper folder.
- Sequential scheduling engine designed to avoid overlapping changes and timer backlogs.
- Windows monitor discovery and per-monitor local wallpaper assignment through the desktop wallpaper API.
- Unit tests, Windows CI workflow, build script, product specification, technical design and clean-room provenance notes.

Windows exposes one shared wallpaper fit mode. Independent fit modes per monitor still require an image-composition layer.

The source is available on [the foundation branch](https://github.com/DetectiveLon/oriole-wallpaper-manager/tree/foundation/0.1.0). Draft PR #1 is open for review and has not been merged.

## Not implemented yet

- Persistent searchable library, favourites, collections and metadata
- Per-display automation UI, persisted schedules, pause/resume and fullscreen-game deferral
- Tray controls and startup integration
- Independent fit modes per monitor
- Installer and release packaging

The Wallhaven API key entered in the discovery page is used for requests and is not persisted. Images fetched from Wallhaven remain subject to their original owners' rights.

## Repository layout

`main` retains the project-status README. Working source is developed on separate branches and proposed through pull requests.
