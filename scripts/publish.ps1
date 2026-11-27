<#
.SYNOPSIS
Builds and packages the self-contained, single-file win-x64 release for Ori.

.DESCRIPTION
This is the SINGLE SOURCE OF TRUTH for release packaging. It:
  1. resolves a tag-driven version ( -Tag > -Version > csproj <Version> ),
  2. runs the Core and desktop helper tests,
  3. publishes the WPF app as self-contained + single-file + win-x64,
  4. stamps the version into the assembly AND the application manifest,
  5. writes README.md and a generated RELEASE-NOTES.txt (no runtime prerequisite),
  6. produces a portable zip (app + notes + updater), a standalone update EXE and
      an update manifest whose version/asset alignment is verified before exit,
  7. prints absolute paths and SHA-256 hashes.

No secrets, no code signing and no installer are involved. Requires the exact
.NET 10 SDK pinned by global.json (10.0.404). Inno Setup is optional and is NOT
invoked here; see tools/installer/README.md.

.EXAMPLE
  pwsh ./scripts/publish.ps1 -Tag v1.0.0
  pwsh ./scripts/publish.ps1 -Version 1.0.0
  pwsh ./scripts/publish.ps1 -SkipTests        # used by build.ps1 -Publish
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Tag,
    [string]$Repository = 'DetectiveLon/ori-wallpaper-manager',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

Push-Location $repoRoot
try {
    # --- Resolve the one tag-driven version ----------------------------------
    if ([string]::IsNullOrWhiteSpace($Version)) {
        if (-not [string]::IsNullOrWhiteSpace($Tag)) {
            $Version = $Tag -replace '^v', ''
        }
        else {
            $csproj = Join-Path $repoRoot 'src/Ori.Desktop/Ori.Desktop.csproj'
            [xml]$projectXml = Get-Content -Path $csproj -Raw
            $Version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Could not resolve a release version. Pass -Version or -Tag.'
    }
    if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Release version '$Version' must be numeric (e.g. 1.0.0); tag-driven releases do not use prerelease suffixes."
    }

    $safeVersion = ($Version -replace '[^A-Za-z0-9._-]', '-').Trim('-')
    $releaseTag  = if ([string]::IsNullOrWhiteSpace($Tag)) { "v$Version" } else { $Tag }

    # assemblyIdentity wants four parts; the app version is three.
    $vparts = $Version.Split('.')
    $assemblyVersion = '{0}.{1}.{2}.0' -f $vparts[0], $vparts[1], $vparts[2]

    $artifactsDir = Join-Path $repoRoot 'artifacts'
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

    # --- Tests ---------------------------------------------------------------
    if (-not $SkipTests) {
        Write-Host "==> Running Core tests for Ori $Version"
        dotnet test tests/Ori.Core.Tests/Ori.Core.Tests.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
        Write-Host "==> Running desktop helper tests for Ori $Version"
        dotnet test tests/Ori.Desktop.Tests/Ori.Desktop.Tests.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
    }
    else {
        Write-Host '==> Skipping tests (caller already ran them).'
    }

    # --- Patch the app manifest so its identity version matches the tag -------
    $manifestSrc  = Join-Path $repoRoot 'src/Ori.Desktop/app.manifest'
    $manifestWork = Join-Path $artifactsDir "manifest-$safeVersion"
    New-Item -ItemType Directory -Force -Path $manifestWork | Out-Null
    $manifestOut  = Join-Path $manifestWork 'app.manifest'
    $manifestText = Get-Content -LiteralPath $manifestSrc -Raw
    $patched = [regex]::Replace(
        $manifestText,
        '(<assemblyIdentity\b[^>]*?\bversion=")[^"]*(")',
        { param($m) $m.Groups[1].Value + $assemblyVersion + $m.Groups[2].Value })
    if ($patched -eq $manifestText -and $manifestText -notmatch "$assemblyVersion") {
        throw 'Could not stamp the assemblyIdentity version into app.manifest.'
    }
    Set-Content -LiteralPath $manifestOut -Value $patched -Encoding UTF8

    # --- Publish into a staging directory ------------------------------------
    $stageDir = Join-Path $artifactsDir "stage-$safeVersion"
    if (Test-Path $stageDir) { Remove-Item -Path $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    Write-Host '==> Publishing self-contained single-file win-x64 build'
    dotnet publish src/Ori.Desktop/Ori.Desktop.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        "-p:Version=$Version" `
        "-p:ApplicationManifest=$manifestOut" `
        --output $stageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $publishedExe = Join-Path $stageDir 'Ori.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Publish did not produce $publishedExe."
    }

    # --- Stage the separate updater helper in the same portable folder --------
    # Auto-update needs Ori.Updater.exe next to Ori.exe. Publishing it into the
    # staging directory (rather than copying it later) keeps the zip self-contained.
    Write-Host '==> Publishing Ori.Updater.exe into the portable package'
    dotnet publish tools/Ori.Updater/Ori.Updater.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        "-p:Version=$Version" `
        --output $stageDir
    if ($LASTEXITCODE -ne 0) { throw "Updater publish failed with exit code $LASTEXITCODE." }
    $publishedUpdater = Join-Path $stageDir 'Ori.Updater.exe'
    if (-not (Test-Path -LiteralPath $publishedUpdater)) {
        throw "Updater publish did not produce $publishedUpdater."
    }

    # --- Add documentation ---------------------------------------------------
    Copy-Item -Path (Join-Path $repoRoot 'README.md') `
        -Destination (Join-Path $stageDir 'README.md') -Force

    $notes = @"
Ori $Version - portable win-x64 build

Ori is a clean-room Windows 11 wallpaper manager. Extract this folder and run
Ori.exe. This build is SELF-CONTAINED: the .NET runtime is bundled, so there is
no .NET Desktop Runtime to install.

Included here:
  * Ori.Updater.exe - the separate helper used by automatic updates.

Optional (not included here):
  * A per-user Inno Setup installer: see tools/installer/README.md.
  * Automatic update wiring: see docs/RELEASING.md.

This package is not code-signed. Windows SmartScreen may warn on first run until
the app builds reputation; signing is deferred, not ignored. It contains no API
keys or secrets. Wallhaven images remain the property of their original owners.
"@
    Set-Content -Path (Join-Path $stageDir 'RELEASE-NOTES.txt') -Value $notes -Encoding UTF8
    # Also expose it at artifacts/ so the optional Inno Setup build can include it.
    Copy-Item -LiteralPath (Join-Path $stageDir 'RELEASE-NOTES.txt') `
        -Destination (Join-Path $artifactsDir 'RELEASE-NOTES.txt') -Force

    # --- Package: zip + standalone EXE + SHA-256 -----------------------------
    $zipName = "ori-$safeVersion-win-x64.zip"
    $zipPath = Join-Path $artifactsDir $zipName
    if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force
    $zipHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash

    $exeName = "ori-$safeVersion-win-x64.exe"
    $exePath = Join-Path $artifactsDir $exeName
    Copy-Item -LiteralPath $publishedExe -Destination $exePath -Force
    $exeHash = (Get-FileHash -Algorithm SHA256 -Path $exePath).Hash

    # --- Update manifest consumed by tools/Ori.Updater ---------------------
    # Attached as a release asset; fetched from
    # https://github.com/<repo>/releases/latest/download/update-manifest.json
    $manifest = [ordered]@{
        version     = $Version
        assetName   = $exeName
        sha256      = $exeHash.ToLowerInvariant()
        url         = "https://github.com/$Repository/releases/download/$releaseTag/$exeName"
        publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }
    $manifestPath = Join-Path $artifactsDir 'update-manifest.json'
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    # Parse the file we just wrote back: a manifest that disagrees with its own
    # asset is worse than no manifest at all, so fail the publish immediately.
    $written = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($written.version -ne $Version -or
        $written.assetName -ne $exeName -or
        $written.sha256 -ne $exeHash.ToLowerInvariant() -or
        $written.url -ne $manifest.url) {
        throw 'Generated update-manifest.json is not aligned with the published EXE.'
    }

    # Keep the raw EXE for the optional Inno Setup build.
    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $artifactsDir 'Ori.exe') -Force

    Remove-Item -Path $stageDir -Recurse -Force
    Remove-Item -Path $manifestWork -Recurse -Force

    Write-Host ''
    Write-Host 'Release package ready.'
    Write-Host "Zip:         $zipPath"
    Write-Host "SHA-256:     $zipHash"
    Write-Host "Updater EXE: $exePath"
    Write-Host "SHA-256:     $exeHash"
    Write-Host "Manifest:    $manifestPath"
}
finally {
    Pop-Location
}
