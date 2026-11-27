# Product specification

## Goal

A static-wallpaper manager for Windows 11 that makes finding, previewing, saving, assigning, and rotating wallpapers straightforward. The app should be useful with one monitor but treat multi-monitor as a first-class case.

## Information architecture

1. **Discover** (landing page) - search and curated filters; preview; set now; save to library; add to a collection; tag presets; save the current filters as a named rotation feed.
2. **Library** - all saved files, collections, favourites, local-folder import, duplicate detection, and search.
3. **Displays** - a scaled diagram of the real monitor layout; select a display; choose source/image; fit mode with a live preview; span control.
4. **Auto rotation** - global and per-display schedules, Wallhaven rotation feeds with cadence and round-robin cycling, exact next-run time, previous, pause, resume, and fullscreen deferral.
5. **Settings** - appearance, startup (including start-minimized-to-tray, on by
   default), update status, cache, provider credentials, and privacy.

(The former **Overview** screen was removed in `v2-shell`; Discover is the landing page. The former **Automation** page is now called Auto rotation; its internal key is unchanged.)

## Discovery filters

Wallhaven provider requirements:

- Categories: General, Anime, People, independently selectable.
- Purity: SFW, Sketchy, NSFW, independently selectable. NSFW must be off by default and requires a Wallhaven key.
- Search expression with required (`+tag`) and excluded (`-tag`) terms.
- Sorting: date added, relevance, random, views, favorites, toplist, hot.
- Toplist period: day, 3 days, week, month, 3 months, 6 months, year.
- Minimum resolution and one exact resolution selection.
- One aspect-ratio selection, including landscape, ultrawide, portrait, and square presets.
- Pagination and random seed preservation so pages do not unexpectedly repeat.
- Color palette filters using the provider’s documented palette.

## Desktop behaviour

- Each connected monitor has a stable identity and independent assignment.
- Support Fill, Fit, Stretch, Center, Tile, and a unified span mode where technically appropriate.
- Preview the final crop/fill before applying.
- An apply operation is transactional from the UI's perspective: preserve the prior wallpaper if download, decode, render, or apply fails.
- All downloads have a maximum byte count; all decoded images have dimension/pixel limits.

## Rotation feeds

- A feed is a named Wallhaven filter set with its own cadence (15 minutes to 7 days), enable state, and fetch/apply history. Users save feeds explicitly from Discover and manage them on Auto rotation.
- Feeds are ordered. Fetching cycles round-robin through enabled feeds that are due, skipping feeds still in backoff. The master Wallhaven opt-in gates all feeds.
- Fetch failures use exponential backoff from 30 seconds to 15 minutes, and that state is persisted so a restart cannot reset it and hammer the provider.
- A fetched image is applied on the next rotation for the target display, or immediately when "Fetch now" is used. Fetching is serialized app-wide so many displays cannot overlap requests.
- Existing single-feed settings migrate silently to one feed named "Saved search", preserving the opt-in and cadence. The API key is never written into feed settings; it stays protected by DPAPI.

## Scheduler contract

- One active worker per display; no overlapping requests for the same display.
- Interval countdown is visible and derives from an absolute next-run timestamp.
- The next interval starts after a rotation attempt completes, not while a previous request is still running.
- Missed ticks after sleep/restart are skipped, never replayed as a burst.
- Fullscreen/game pause is rechecked immediately before applying the wallpaper.
  Detection enumerates all visible top-level windows against each display's
  geometry, not just the foreground window, and remembers a blocker through brief
  focus changes.
- Pause/resume and next/previous are explicit user actions.
- Errors are visible and logged; the current wallpaper remains unchanged on failure.
- Scheduling state survives app restarts without performing a surprise immediate rotation.

## Performance and privacy

- Static images only. No continuous video decode or GPU animation loop.
- Idle app should be event-driven and use negligible CPU.
- Bounded thumbnail cache and bounded full-size download cache.
- No telemetry or account requirement.
- API keys protected with Windows DPAPI scoped to `CurrentUser`; never written into plaintext settings. Discover no longer auto-saves the key; save it explicitly in Settings.
- Application errors are written to a rotating, redacted `errors.log` under Local App Data.
- No image is applied or downloaded without a visible user action or configured schedule.

## Packaging and updates

- The shipped build is self-contained and single-file for `win-x64`: no .NET runtime is required on the target PC.
- A free, optional per-user installer installs binaries to `%LOCALAPPDATA%\Programs\Ori`, reuses the app's launch-at-sign-in setting, and never deletes user data under `%LOCALAPPDATA%\Ori`.
- Updates are checked by a **separate** process (`Ori.Updater`) against a GitHub Releases JSON manifest. The portable zip bundles `Ori.Updater.exe`; `App.OnStartup` runs a non-blocking `check`, and the result appears in Settings and a tray balloon. The apply/swap step remains a deliberate manual update for now (see `docs/RELEASING.md`).
- Binaries are not code-signed; SmartScreen reputation is deferred, not ignored.
