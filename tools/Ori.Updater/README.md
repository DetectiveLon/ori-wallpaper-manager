# Ori.Updater

A tiny console helper that lets Ori update itself. It is a **separate
process** because a running Windows EXE cannot overwrite itself.

## Contract

The release pipeline (`scripts/publish.ps1`) publishes an update manifest as a
GitHub Release asset:

```text
https://github.com/DetectiveLon/ori-wallpaper-manager/releases/latest/download/update-manifest.json
```

The manifest is JSON:

```json
{
  "version": "1.0.0",
  "assetName": "ori-1.0.0-win-x64.exe",
  "sha256": "....",
  "url": "https://github.com/.../releases/download/v1.0.0/ori-1.0.0-win-x64.exe",
  "publishedAt": "2026-11-23T09:00:00.0000000Z"
}
```

`version` is the single tag-driven version and must equal the app's
`AssemblyInformationalVersion`.

## Commands

```powershell
# Check only. Prints a JSON CheckResult to stdout; always exits 0 on success.
Ori.Updater.exe check --exe "C:\Path\Ori.exe"

# Apply. Downloads + verifies BEFORE touching the running app, waits for the
# parent PID to exit, backs up the old EXE, swaps in the new one, relaunches.
# A successful swap deletes the .bak; on failure the old EXE is restored.
Ori.Updater.exe apply --exe "C:\Path\Ori.exe" --pid 1234 --relaunch
```

Both commands accept `--manifest-url <https-url>` for testing (for example a
local HTTPS endpoint). Only HTTPS URLs are accepted, including every redirect
hop: automatic redirects are disabled and a downgrade to `http://` is refused.
Downloads are capped at 200 MB and SHA-256 is verified before any file is
replaced. On failure the old EXE is restored from its `.bak`.

## Integration status

The updater is built by CI, staged into the portable zip, installed next to
`Ori.exe` by the optional Inno Setup installer, and shipped with the release
manifest. The **startup version check is wired**: `App.OnStartup` in
`src/Ori.Desktop/App.xaml.cs` calls `UpdateChecker.CheckOnStartupAsync()`, which
runs `Ori.Updater.exe check` once
per process and reports the result in Settings and via a tray balloon. There is
deliberately no in-app "apply now" button: applying an update is still a manual
download/install. See `docs/RELEASING.md` for the release contract.
