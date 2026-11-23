# Implementation status

Current working branch: `v2-shell` (v1 work remains on `feature/library-automation`)

This is a progress record, not a release-readiness claim.

## Implemented

- Existing Wallhaven discovery page retained from the foundation branch.
- Wallhaven search categories, purity, query expressions, sorting, toplist periods, order, resolution, aspect ratio, and color filters.
- Search pagination and thumbnail previews.
- Full-size preview, local saving, and apply to a selected display.
- Bounded original/thumbnail downloads and image decode limits.
- Versioned JSON library index under Local AppData.
- Library search, favourites-only filter, apply, open image file, and metadata-only removal.
- Independent app-session rotation schedules for each connected display, each with its own interval and controls.
- Per-display countdown, manual rotate, pause/resume, stop, serialized wallpaper apply, and a fresh interval after a pause.
- Versioned per-display schedule state under Local AppData; running schedules resume after an app restart, and missed deadlines restart with a fresh full interval.
- Optional pause when a full-screen foreground window covers the target display. Geometry matching tolerates small window-frame rounding but rejects ordinary maximized windows that leave the taskbar area.
- App navigation does not stop rotation; main-window close requests cancellation.
- Settings page with DPAPI-protected Wallhaven API key storage, cache clearing, data folders, and version information.
- Dark-theme styles for buttons, combo boxes and scrollbars, including disabled states.

## Not implemented

- Identifying games specifically by process or executable name; current detection applies to full-screen foreground apps generally.
- Detecting background full-screen windows when they are not the foreground window.
- Library collections, local-folder import, and duplicate detection.
- Independent per-monitor fit modes.
- Tray controls, startup registration, installer, and release packaging.
- Library virtualization and a collections UI (the library decodes thumbnails off the UI thread and debounces filtering, but renders all matching cards).

## V2 shell branch

`v2-shell` replaces the code-built pages with XAML views and view models (`ViewModels/`, `Views/*View.xaml`, `Mvvm/`) while reusing the unchanged services and Core libraries. All six screens were smoke-tested on a two-monitor Windows desktop; the old page classes were removed on that branch. It is a presentation-layer rewrite, not a feature release — installer, tray, collections and virtualization are still outstanding.

## Validation

GitHub Actions now allocates hosted runners after the repository became public. The latest PR validation passed the Core test job and the Windows desktop build. This confirms compilation and the automated Core tests, but does not replace an interactive check on a real multi-monitor Windows desktop. A live two-monitor session verified detection, grid cards, per-display controls, and schedule restore after restart. The app has not yet been packaged or released.
