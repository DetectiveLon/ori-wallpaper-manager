# Releasing Ori

This describes the free, open-source release process. Ori ships as a
**self-contained, single-file win-x64** build, so there is no .NET runtime
prerequisite. An optional per-user Inno Setup installer and an auto-update
helper are also produced. Code signing is deferred (see the last section).

## Single version source

There is exactly one version source: `src/Ori.Desktop/Ori.Desktop.csproj`
(`<Version>`, currently `1.0.0`), overridden at release time by the tag. The
tag `v1.0.0` becomes version `1.0.0`, which the SDK stamps into
`AssemblyVersion`, `FileVersion` and **`AssemblyInformationalVersion`**. The
About page derives the displayed version from `AssemblyInformationalVersion`.
`scripts/publish.ps1` also rewrites `app.manifest`'s `assemblyIdentity` version
in the published copy so all three can never disagree.

Do not hand-edit version numbers in individual files; always change the single
source and let the tag override it.

## What gets released

For tag `v1.0.0`, `artifacts/` contains:

- `ori-1.0.0-win-x64.zip` — portable zip (self-contained `Ori.exe`,
  `Ori.Updater.exe`, `README.md`, `RELEASE-NOTES.txt`).
- `ori-1.0.0-win-x64.exe` — standalone EXE used by auto-update.
- `update-manifest.json` — JSON manifest consumed by `Ori.Updater`.
- `Ori.exe` — the raw single-file EXE used by the optional Inno Setup build.

All are attached to the GitHub Release and uploaded as a workflow artifact;
their SHA-256 hashes are printed by the script and written into the manifest and
release notes.

## Prerequisites

- The release is built by GitHub Actions; you need permission to push tags and
  create releases.
- The SDK is pinned exactly to **10.0.404** in `global.json` and in both
  workflows. No floating `10.0.x`.
- Locally: Windows and the .NET 10 SDK (10.0.404).

## Cut a release

1. Make sure the code you want to ship is merged and its CI checks are green.
   The **Release** workflow has a `verify` job that runs restore, Core tests,
   the full solution build, the desktop helper tests and all static guards
   (`check-buttons.ps1 -Strict` included) before publishing. A red commit cannot
   be tagged into a release. CI separately runs Core tests on both Linux and
   Windows so the Windows-only path branch is covered on every PR.
2. Create and push a tag matching the intended version:

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. Pushing a `v*` tag starts the **Release** workflow on Windows. It verifies,
   runs `scripts/publish.ps1 -Tag <tag> -SkipTests`, computes checksums, uploads
   a workflow artifact and creates the GitHub Release with the zip, standalone
   EXE and update manifest attached.
4. Confirm the run passed under **Actions**, then open **Releases** and spot-check
   one hash from the notes against the downloaded file.

## Build a package locally (optional)

```powershell
pwsh ./scripts/publish.ps1 -Tag v1.0.0
# or
pwsh ./scripts/publish.ps1 -Version 1.0.0
```

With no argument the version is read from `Ori.Desktop.csproj`. The script
runs the Core tests, publishes self-contained/single-file/win-x64, writes the
notes and manifest, and prints absolute paths and SHA-256 hashes. Output goes
into `artifacts/` (gitignored).

## Optional per-user installer

Inno Setup is free and optional. See `tools/installer/README.md` and
`tools/installer/build-installer.ps1`. It produces `ori-<version>-setup.exe`
that installs binaries to `%LOCALAPPDATA%\Programs\Ori`, adds Start
menu/uninstall entries and reuses the app's own launch-at-sign-in setting. User
data lives in the separate `%LOCALAPPDATA%\Ori` folder and is never deleted by
uninstall. It is not built by CI by default.

## Auto-update: check wired, apply still manual

Built and shipped:

- `tools/Ori.Updater` — a separate console process that fetches
  `update-manifest.json`, compares `version` against the running app's
  `AssemblyInformationalVersion` and verifies SHA-256. Its `apply` mode can wait
  for the app to exit, back up the old EXE, swap in the new one and relaunch.
  It is added to `Ori.sln`, built by CI, **and staged into the portable zip** by
  `scripts/publish.ps1`.
- The release pipeline publishes the manifest and standalone EXE as assets and
  parses the generated manifest back to confirm version/asset alignment.

**Wired in this wave:** `App.OnStartup` calls
`Ori.Desktop.Services.UpdateChecker.CheckOnStartupAsync()`. The checker launches
the bundled `Ori.Updater.exe check --exe <running Ori.exe>` once per process in
the background, with a 12-second timeout and silent failure on network/JSON
errors. It puts short status text in Settings (`UpdateStatus`) and raises a tray
balloon when an update is available. A missing updater (development builds) is a
no-op.

**Still manual:** there is deliberately no in-app "apply now" button. Users
download the new release from GitHub and run the installer or replace the zip.
The main app cannot overwrite its own running EXE; the check and swap must live
in the separate updater process. A live end-to-end check against a real release
is still pending, so treat the network path as unverified.

## What is not inside the zip

- No installer (a separate, optional `.iss` builds one).
- No code signature (see below).
- No .NET runtime to install — the build is self-contained.
- No API keys, secrets or other credentials.

## Signing / SmartScreen — deferred, not ignored

These binaries are **not code-signed**. Windows SmartScreen may warn on first
run until the app builds reputation. Code signing requires a certificate that
is not available yet; it is explicitly tracked as deferred rather than silently
ignored. The SHA-256 checksums above let users verify integrity in the meantime.
