# Technical design

## Layers

- `Oriole.Core`: provider contracts and implementations, filter models, scheduling rules, persistent settings models, cache rules, and image metadata. No WPF references and no Windows COM types.
- `Oriole.Desktop`: WPF user interface, Windows monitor discovery, wallpaper application, tray integration, startup registration, DPAPI, and Windows session/fullscreen detection.
- `Oriole.Core.Tests`: fast deterministic tests for all platform-independent behaviour.

## Provider boundary

Providers return metadata and safe HTTPS image/page URIs. The provider client does not directly apply images. A separate download service will validate host/scheme, byte limits, file signatures, and decode dimensions before an image enters the local library or render pipeline.

## Scheduling

The current `RotationLoop` is intentionally sequential. It awaits a due time, checks the pause condition, awaits one rotation attempt, then computes the next due time from completion. It has no periodic timer callback and therefore cannot accumulate queued ticks while network or disk operations are slow.

The desktop layer will own one loop per display, each with its own cancellation token. Reconfiguration cancels and disposes the old loop before a new one is started. The persisted state will store the next due instant in UTC while the UI displays local time.

## Windows desktop boundary

Windows-only operations will be accessed through interfaces (monitor inventory, wallpaper apply, fullscreen detection, startup registration, secret storage). This keeps all business logic and most tests independent of desktop APIs.

## Persistence

Settings and collection metadata will be stored as versioned JSON with write-to-temp then atomic replace. Credentials are stored separately through DPAPI. The image library is ordinary image files plus a small metadata database; the initial version will use SQLite only if the actual data model requires transactional querying, otherwise JSON metadata remains simpler.

## Safety limits

- JSON API response: 2 MiB maximum.
- Image download: 50 MiB maximum initially, configurable only downward until tested.
- Decoded image: reject unreasonable dimensions and total pixels before allocating render buffers.
- Provider response URLs must be HTTPS and match the provider's approved host policy.
- Cancellation is passed through every network/disk operation.
