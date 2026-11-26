# Ori backlog

Captured from the user's product discussion on 2026-11-23. Status below is
updated for the `v2-shell` branch. This is a backlog, not a commitment.

## P1

- [x] **Installer and auto-update.** The release is now a self-contained,
  single-file win-x64 build (no .NET runtime needed). A free, optional per-user
  Inno Setup installer builds from `tools/installer/`; a separate
  `tools/Ori.Updater` process checks a GitHub Releases manifest, verifies
  SHA-256 and swaps the EXE. The auto-update check is only *documented* as an
  integration step in `App.xaml.cs` (owned by another agent) — see
  `docs/RELEASING.md`. Signing/SmartScreen is deferred, not ignored.
- [x] **Automatic Wallhaven rotation.** Implemented in `v2-shell`: configurable
  tag feeds are persisted (`WallhavenFeedStore`) and drive fetch/rotate/cache
  without a manual per-image search/download.

## P2

- [x] **Smarter tag discovery.** Implemented in `v2-shell`: tag presets and an
  editable tag/collection ComboBox (`IsEditable="True"`) replace the old
  free-text-only box. A learned preference model is not implemented.
- [x] **Clearer status and notifications.** Implemented in `v2-shell`: tray
  balloon notifications (`TrayIconService.Notify`) plus inline status on every
  page.
- [x] **Real image import and organization.** Implemented in `v2-shell`:
  local-folder import, SHA-256 duplicate detection and collections in
  `WallpaperLibraryStore` and the library view. Rename is not implemented.

## Constraints

- No paid services, models or tools.
- Keep the tested `Ori.Core` layer intact where possible.
- GitHub repo is public; no secrets in the repository.
