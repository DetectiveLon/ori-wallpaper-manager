# Ori backlog

Captured from the user's product discussion on 2026-11-23. Status below is
updated for the `v2-shell` branch. This is a backlog, not a commitment.

## P1

- [x] **Installer and auto-update.** The release is now a self-contained,
  single-file win-x64 build (no .NET runtime needed). A free, optional per-user
  Inno Setup installer builds from `tools/installer/`; a separate
  `tools/Ori.Updater` process checks a GitHub Releases manifest, verifies
  SHA-256 and swaps the EXE. The startup check is wired through
  `UpdateChecker.CheckOnStartupAsync()` in `App.OnStartup`; applying an update
  is still a manual download/install — see `docs/RELEASING.md`.
  Signing/SmartScreen is deferred, not ignored.
- [x] **Automatic Wallhaven rotation.** Implemented in `v2-shell`: an ordered
  list of named feeds with per-feed cadence, enable state and persisted fetch
  backoff lives in `WallhavenFeedStore`. Discover saves filters as a feed; Auto
  rotation cycles through due feeds, fetches and caches images, and applies the
  fetched image on the next rotation or immediately with Fetch now. Legacy
  single-feed settings migrate to a feed named "Saved search".

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
