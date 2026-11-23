# Oriole QA findings — feature/library-automation @ 34b2dba

Live-tested on the user's Windows 11 desktop with SDK 10.0.404, Release build from `build.ps1`.
Screenshots live under `C:\Users\eslam\AppData\Local\hermes\cache\images\`.

## Final status (this session)

- Fixed 14 issues; 9 new regression tests. 45/45 core tests pass, WPF build 0 warnings/0 errors.
- Final smoke: app launches, both monitors detected, Displays and Automation render, no modal; a seeded schedule restored with a live countdown; graceful close now exits cleanly. Live close testing found and fixed a WPF re-entrant `Close()` bug in the first shutdown implementation.
- Fixed in addition to item 1 below: silent process exit (global exception handlers + wrapped library handlers); Library UI decoded every image on the UI thread and re-rendered per keystroke (now background decode + 180 ms debounce); Start error overwritten by cleanup; wallpaper apply forcing global Fill fit mode; Discover Search button off-screen; blocking detection modal; reset-during-pause double notification; throwing loop callbacks killing rotation; API key forwarded on auto-redirecting HttpClient; no path back to "Unsorted"; relative index path crash; invalid WallhavenOrder silently mapping to "desc"; stale Overview copy; CTA highlighted as nav.
- Still open: duplicated detection presentation in three pages (detection itself now shares one service); primary-monitor fallback by index; two disagreeing image pipelines; dead Settings page; foreground-only full-screen detection; schedule persistence does not survive moving to a different monitor ID.
- Not tested: real Wallhaven search/download, full-screen pause with a game, installer.

## Fixed during this session

### 1. High — one stale display path made *all* display detection fail
- Where: `src/Oriole.Desktop/Services/WindowsDesktopWallpaper.cs:46-56` (original).
- Repro: open Discover/Displays/Automation; banner "Display detection unavailable: Unexpected HRESULT has been returned from a call to a COM component."; Displays opened a modal "Display detection failed".
- Root cause (proved with an STA probe against the compiled DLL): `IDesktopWallpaper.GetMonitorDevicePathCount` returns 4 paths; paths 2 and 3 are stale duplicate instances (`&0&` instead of `&1&`) for the same two panels. `GetMonitorRECT` throws `COMException 0x80004005` on those. The loop did not catch it, so the whole enumeration aborted and the app reported zero displays.
- Fix: catch `COMException` per monitor path and skip it; also tolerate a failed `GetWallpaper` read. Healthy monitors are still returned with geometry, primary flag and wallpaper.
- Verified: probe now returns 2 monitors (`\\.\DISPLAY1` 1920x1080 primary, `\\.\DISPLAY2` 1280x1024 at -1280,55); UI Displays page lists both cards; Automation page now renders two independent schedule panels. 36/36 tests still pass, build 0 warnings/0 errors.
- Screenshots: `computer_use_0a5a99877dc14c649bba30d5a5523790.png` (error), `computer_use_09d579fa538c47e2ac53dc0beebd5df2.png` (fixed).

## Initially open findings (superseded by the final status above where noted)

### 2. Medium — blocking modal for a recoverable detection failure — FIXED
- Where: `src/Oriole.Desktop/MainWindow.xaml.cs:149-153`.
- `LoadDisplays` shows `MessageBox.Show` on any exception. A stale monitor or a transient COM failure freezes the page behind a dialog and requires a click before the user can do anything else. Discover and Library show the same failure as an inline banner instead; behaviour is inconsistent across pages.
- Expected: inline status text plus a Refresh action, never a modal.

### 3. Medium — duplicated display detection, three different error presentations
- `MainWindow.xaml.cs`, `Views/DiscoverPage.cs:243-247`, `Pages/LibraryPage.cs:94-98` each call detection and catch exceptions separately. Any change to detection semantics has to be made in three places; the UI can disagree about how many displays exist.
- Expected: single shared snapshot/refresh service.

### 4. Medium — Discover filter column overflows the window
- At the app's default ~1208x740 window the filter pane's bounds run to native y≈1415 while the window ends at y≈908. The "Search wallpapers" button sits at y≈1345, below the visible window, reachable only by scrolling a narrow inner scrollbar. First-run users will not find Search.
- Expected: responsive layout with Search visible or a full-width sticky action bar.

### 5. Low — Settings is a dead end
- Page says theme/startup/cache/download folder/API keys/privacy "will live here"; no controls exist. NSFW search explicitly requires an API key that the user has nowhere to enter.

### 6. Low — Overview copy is stale
- Bottom banner still says "Wallpaper discovery and desktop integration are being added in separate, testable modules", although both are implemented on this branch.

### 7. Low — empty-library actions are enabled
- With 0 saved wallpapers, Start and Rotate now remain enabled and only report an error after clicking ("No available images are saved in the local library"). Expected: disabled with the reason visible.

### 8. Unconfirmed — app process/window state changed between checks
- PID 20128 was alive and responding, then was gone on the next check (no Application event-log error found); PID 20448 later appeared minimized without any action from this session. No crash evidence, so not classified as a defect yet. Needs a reproducible observation.

## Not tested
- Real Wallhaven search/download (no API key configured; network path not exercised).
- Full-screen pause with an actual game: foreground-only detection means switching focus away from the game hides the condition by design.
- Schedule persistence across restarts: documented as not implemented.
- Installer/release package: none exists.
