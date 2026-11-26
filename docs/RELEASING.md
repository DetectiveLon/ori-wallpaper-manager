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
  `README.md`, `RELEASE-NOTES.txt`).
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
   the full solution build and all static guards before publishing. A red
   commit cannot be tagged into a release.
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
that installs to `%LOCALAPPDATA%\Ori`, adds Start menu/uninstall entries and
reuses the app's own launch-at-sign-in setting. It is not built by CI by
default.

## Auto-update: what is built and what is NOT wired

Built and shipped:

- `tools/Ori.Updater` — a separate console process that fetches
  `update-manifest.json`, compares `version` against the running app's
  `AssemblyInformationalVersion`, verifies SHA-256, and (on `apply`) waits for
  the app to exit, backs up the old EXE, swaps in the new one and relaunches.
  It is added to `Ori.sln` and built by CI.
- The release pipeline publishes the manifest and standalone EXE as assets.

**Not wired (documented integration step):** the automatic version check is not
yet called from the app. `App.xaml.cs` is owned by another agent. The exact
integration, once that file is available for edit, is to call the updater in
`OnStartup` after the tray is initialized and before/around showing the main
window, for example:

```csharp
// In Ori.Desktop/App.xaml.cs, inside OnStartup after Tray.Show(...):
_ = Ori.Desktop.Services.UpdateChecker.CheckOnStartupAsync();
```

Where `UpdateChecker.CheckOnStartupAsync()` is a thin wrapper that launches the
bundled `Ori.Updater.exe check` with `--exe <path to running Ori.exe>`,
parses the JSON `CheckResult` from stdout, and — if `updateAvailable` is true —
offers the user a "Update now" action that launches
`Ori.Updater.exe apply --exe <path> --pid <current pid> --relaunch`, then
shuts the app down so the updater can swap the file.

Until that one-line call (and its wrapper) is added, users update manually by
downloading the latest release. This is a deliberate boundary: the main app
cannot overwrite its own running EXE, so the check and swap must live in the
separate updater process.

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
