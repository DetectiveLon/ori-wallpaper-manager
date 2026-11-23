# Releasing Oriole

This describes how to publish a free, open-source release of Oriole as a zip file.
There is no installer and no code signing; users simply unzip and run the app.

## What gets released

A single file named `oriole-<version>-win-x64.zip`, published on the GitHub
Releases page and also saved as a workflow artifact for download.

## Prerequisites

- The release is built by GitHub Actions, so you only need permission to push
  tags and to create releases in this repository.
- For a local test run, you need Windows and the .NET 10 SDK.

## Cut a release

1. Make sure the code you want to ship is merged and its CI checks are green.

2. Pick a version and create a tag, where the version matches the tag without the
   leading `v`. For example, for version `0.2.0`:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

   The tag drives the version stamped into the app and the zip filename, so you do
   not need to edit `Oriole.Desktop.csproj` first.

3. Pushing a tag that starts with `v` automatically starts the **Release** workflow
   on a Windows runner. It installs .NET 10, runs the Core tests, publishes the app,
   builds the zip, uploads it as a workflow artifact, and creates a GitHub Release
   with the zip attached.

4. When the workflow finishes, open the **Actions** tab to confirm it passed, then
   open **Releases** to find the new release. Download the attached zip and verify
   it (the release notes and workflow log show its SHA-256 checksum).

## Build a package locally (optional)

From the repository root:

```powershell
pwsh ./scripts/publish.ps1 -Tag v0.2.0
# or
pwsh ./scripts/publish.ps1 -Version 0.2.0
```

If neither argument is given, the version is read from `Oriole.Desktop.csproj`.
The script prints the absolute path of the zip and its SHA-256 hash. Output goes
into `artifacts/`, which is gitignored.

## What is inside the zip

- `Oriole.exe` and the compiled app assemblies (including `Oriole.Core.dll`) for
  `win-x64`, plus the `.runtimeconfig.json` / `.deps.json` files.
- `README.md`.
- `RELEASE-NOTES.txt` with a short summary and checksum context.

The build is **framework-dependent**: it targets `win-x64` but does not bundle the
.NET runtime, so the target PC must have the .NET 10 Desktop Runtime installed.

## What is not inside the zip

- No installer or setup executable (no `.msi`, no self-extracting installer).
- No code signature; the app is unsigned.
- No self-contained runtime (the .NET 10 Desktop Runtime is required).
- No API keys, secrets, or other credentials.
- No source code, tests, or macOS/Linux builds.
