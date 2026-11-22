# Implementation status

This file describes the current `feature/discover` branch. It is not a release-readiness claim.

## Implemented in this branch

- Wallhaven query filters and API search integration.
- Discovery results with bounded JPEG/PNG download and thumbnail caching.
- Full-image preview and selected-display wallpaper assignment.
- Persistent local library metadata, favourites, filtering, file opening, and metadata-only removal.
- Single-display, app-session rotation from saved local images.
- Live countdown, manual rotate, pause/resume, stop, and no catch-up burst after a pause.
- Unit tests for query construction and the added sort/filter checks.

## Not implemented yet

- Per-display independent rotation schedules.
- Fullscreen-game detection and automatic deferral.
- Persisting rotation settings and restarting schedules after app restart.
- Library collections, local-folder import, duplicate detection, and library management beyond favourites.
- Independent per-monitor fit modes.
- Tray controls, startup registration, installer, and release packaging.
- Encrypted persistent Wallhaven API key storage.

## Validation status

The GitHub Actions workflow has failed before runner assignment on the observed runs. The jobs had no steps or logs, so those failures do not establish whether the code compiles or whether tests pass. The current tool environment also has no .NET SDK installed. A successful Windows build and test run is still required before merging.
