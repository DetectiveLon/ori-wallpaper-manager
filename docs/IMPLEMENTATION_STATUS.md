# Implementation status

Current working branch: `feature/library-automation`

This is a progress record, not a release-readiness claim.

## Implemented

- Existing Wallhaven discovery page retained from the foundation branch.
- Wallhaven search categories, purity, query expressions, sorting, toplist periods, order, resolution, aspect ratio, and color filters.
- Hot sorting exposed in the Discover UI and query builder.
- Search pagination and thumbnail previews.
- Full-size preview, local saving, and apply to a selected display.
- Bounded original/thumbnail downloads and image decode limits.
- Versioned JSON library index under Local AppData.
- Library search, favourites-only filter, apply, open image file, and metadata-only removal.
- Single-display, app-session rotation from local library files.
- Visible countdown, manual rotate, pause/resume, stop, serialized apply, and no catch-up burst after a long pause.
- App navigation does not stop the rotation loop; main-window close requests cancellation.

## Not implemented

- Fullscreen-game detection / automatic pause.
- Independent rotation schedules for each display.
- Persisting schedule state and resuming after app restart.
- Library collections, local-folder import, and duplicate detection.
- Independent per-monitor fit modes.
- Tray controls, startup registration, installer, and release packaging.
- Encrypted persistent storage for the Wallhaven API key.

## Validation

The GitHub Actions workflow has repeatedly failed before runner assignment. The failed jobs contained no steps or logs, so those failures do not establish whether the code compiles or whether tests pass. The current tool environment has no .NET SDK. A successful Windows build and test run is required before merging.
