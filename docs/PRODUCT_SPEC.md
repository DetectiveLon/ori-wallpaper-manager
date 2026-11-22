# Product specification

## Goal

A static-wallpaper manager for Windows 11 that makes finding, previewing, saving, assigning, and rotating wallpapers straightforward. The app should be useful with one monitor but treat multi-monitor as a first-class case.

## Information architecture

1. **Overview** - currently applied image per display, upcoming rotation, pause state, and quick actions.
2. **Discover** - search and curated filters; preview; set now; save to library; add to a collection.
3. **Library** - all saved files, collections, favourites, local folders, and search.
4. **Displays** - interactive monitor layout; select a display; choose source/image; fit mode; preview.
5. **Automation** - global and per-display schedules, exact next-run time, skip, previous, pause, resume, and fullscreen deferral.
6. **Settings** - appearance, startup, cache, download directory, provider credentials, and privacy.

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

## Scheduler contract

- One active worker per display; no overlapping requests for the same display.
- Interval countdown is visible and derives from an absolute next-run timestamp.
- The next interval starts after a rotation attempt completes, not while a previous request is still running.
- Missed ticks after sleep/restart are skipped, never replayed as a burst.
- Fullscreen/game pause is rechecked immediately before applying the wallpaper.
- Pause/resume and next/previous are explicit user actions.
- Errors are visible and logged; the current wallpaper remains unchanged on failure.
- Scheduling state survives app restarts without performing a surprise immediate rotation.

## Performance and privacy

- Static images only. No continuous video decode or GPU animation loop.
- Idle app should be event-driven and use negligible CPU.
- Bounded thumbnail cache and bounded full-size download cache.
- No telemetry or account requirement.
- API keys protected with Windows DPAPI; never written into plaintext settings.
- No image is applied or downloaded without a visible user action or configured schedule.
