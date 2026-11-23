# Oriole per-user installer (optional, free)

`Oriole.iss` is an [Inno Setup 6](https://jrsoftware.org/isinfo.php) script that
produces a small, per-user installer. Inno Setup is free/open source and is
**optional** — the normal release is still the self-contained portable zip from
`scripts/publish.ps1`. Nothing here assumes Inno Setup is installed.

## What it does

- Installs the self-contained, single-file `Oriole.exe` into
  `%LOCALAPPDATA%\Oriole` (no administrator rights needed).
- Adds a Start menu folder and an Add/Remove Programs (uninstall) entry.
- Adds an optional desktop shortcut.
- **Reuses the app's own launch-at-sign-in setting**: the installer deliberately
  does not write `HKCU\...\Run`. That entry is created and managed by Oriole
  itself under Settings → Startup, so the two can never disagree.
- Preserves user data (`library`, `logs`, `schedules`, `feed` subfolders) on
  uninstall; only the files the installer created are removed.

## Build steps

1. Produce the single-file EXE first (from the repository root):

   ```powershell
   pwsh ./scripts/publish.ps1 -Tag v1.0.0
   ```

   This writes `artifacts/Oriole.exe`, `artifacts/update-manifest.json` and the
   versioned zip/EXE.

2. Install Inno Setup 6 from <https://jrsoftware.org/isinfo.php> if you want an
   installer. It is not required for the portable release.

3. Compile the installer (from the repository root):

   ```powershell
   pwsh ./tools/installer/build-installer.ps1
   # or, if iscc.exe is not on PATH:
   pwsh ./tools/installer/build-installer.ps1 -CompilerPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
   ```

   The version is read from `artifacts/update-manifest.json` (or pass
   `-Version 1.0.0`). The result is `artifacts/oriole-<version>-setup.exe`.

You can also compile directly with `iscc /DAppVersion=1.0.0
/DSourceExe=<abs path to Oriole.exe> Oriole.iss`.

## Signing / SmartScreen

The installer and EXE are **not code-signed**. Code signing (and the resulting
SmartScreen reputation) is deferred, not silently ignored; until a certificate
is available, Windows may show a SmartScreen warning on first run. This does not
affect the portable release.
