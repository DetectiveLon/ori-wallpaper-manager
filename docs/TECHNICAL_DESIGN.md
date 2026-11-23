# Technical design

## Layers

- `Oriole.Core`: provider contracts and implementations, filter models, scheduling rules, persistent settings models (including the rotation feed store), cache rules, library import/dedupe, and image metadata. No WPF references and no Windows COM types.
- `Oriole.Desktop`: WPF user interface, Windows monitor discovery, wallpaper application, tray integration and notifications, startup registration, DPAPI (`CurrentUser`), and Windows session/fullscreen detection.
- `Oriole.Core.Tests`: fast deterministic tests for all platform-independent behaviour.
- `tools/Oriole.Updater`: a separate console process (in `Oriole.sln`) that checks a GitHub Releases JSON manifest, verifies SHA-256, and swaps the EXE after the app exits. It is not referenced by the app at runtime — the two communicate only through the manifest and process launch.
- `tools/installer`: an optional Inno Setup `.iss` (per-user, `%LOCALAPPDATA%\Oriole`). Not built by CI by default.

## Provider boundary

Providers return metadata and safe HTTPS image/page URIs. The provider client does not directly apply images. A separate download service will validate host/scheme, byte limits, file signatures, and decode dimensions before an image enters the local library or render pipeline.

## Scheduling

The current `RotationLoop` is intentionally sequential. It awaits a due time, checks the pause condition, awaits one rotation attempt, then computes the next due time from completion. It has no periodic timer callback and therefore cannot accumulate queued ticks while network or disk operations are slow.

The desktop layer will own one loop per display, each with its own cancellation token. Reconfiguration cancels and disposes the old loop before a new one is started. The persisted state will store the next due instant in UTC while the UI displays local time.

## Windows desktop boundary

Windows-only operations will be accessed through interfaces (monitor inventory, wallpaper apply, fullscreen detection, startup registration, secret storage). This keeps all business logic and most tests independent of desktop APIs.

## Persistence

Settings and collection metadata will be stored as versioned JSON with write-to-temp then atomic replace. Credentials are stored separately through DPAPI. The image library is ordinary image files plus a small metadata database; the initial version will use SQLite only if the actual data model requires transactional querying, otherwise JSON metadata remains simpler.

## Persistence (current)

- Settings, schedules, library metadata and rotation feeds are versioned JSON under `%LOCALAPPDATA%\Oriole`, written to temp then atomically replaced.
- Credentials are stored separately through DPAPI with `DataProtectionScope.CurrentUser`.
- `errors.log` rotates (bounded size/history) and redacts sensitive values; the path is surfaced in Settings.

## Packaging and self-update

- Release packaging is centralized in `scripts/publish.ps1`: self-contained + single-file + `win-x64`, tag-driven version, and a generated `update-manifest.json` with the EXE's SHA-256. `build.ps1 -Publish` delegates to it.
- The updater downloads only over HTTPS, caps the asset at 200 MB, verifies SHA-256 before swapping, backs up the running EXE to `.bak` and restores it on failure. The main app cannot overwrite itself, hence the separate process.
- The in-app version-check call is not yet implemented (`App.xaml.cs` is owned by another agent); the exact integration is recorded in `docs/RELEASING.md`.
- Signing is deferred, not silently ignored.

## Safety limits

- JSON API response: 2 MiB maximum.
- Image download: 50 MiB maximum initially, configurable only downward until tested.
- Update asset download: 200 MiB maximum.
- Decoded image: reject unreasonable dimensions and total pixels before allocating render buffers.
- Provider response URLs and update manifest/asset URLs must be HTTPS; provider URLs must match the approved host policy.
- Cancellation is passed through every network/disk operation.
