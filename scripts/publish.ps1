<#
.SYNOPSIS
Builds and packages a portable win-x64 release zip for Oriole.

.DESCRIPTION
Runs the Core tests, publishes the WPF app as a framework-dependent win-x64 build,
adds README.md and a generated RELEASE-NOTES.txt, zips everything into artifacts/,
and prints the absolute zip path and its SHA-256 hash.

No secrets, signing, or installer are involved. Requires the .NET 10 SDK.
#>
param(
    [string]$Version,
    [string]$Tag
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

Push-Location $repoRoot
try {
    # --- Resolve the release version ----------------------------------------
    if ([string]::IsNullOrWhiteSpace($Version)) {
        if (-not [string]::IsNullOrWhiteSpace($Tag)) {
            $Version = $Tag -replace '^v', ''
        }
        else {
            $csproj = Join-Path $repoRoot 'src/Oriole.Desktop/Oriole.Desktop.csproj'
            [xml]$projectXml = Get-Content -Path $csproj -Raw
            $Version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Could not resolve a release version. Pass -Version or -Tag.'
    }

    $safeVersion = ($Version -replace '[^A-Za-z0-9._-]', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safeVersion)) { $safeVersion = 'unknown' }

    $artifactsDir = Join-Path $repoRoot 'artifacts'
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

    # --- Tests ---------------------------------------------------------------
    Write-Host "==> Running Core tests for Oriole $Version"
    dotnet test tests/Oriole.Core.Tests/Oriole.Core.Tests.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

    # --- Publish into a staging directory ------------------------------------
    $stageDir = Join-Path $artifactsDir "stage-$safeVersion"
    if (Test-Path $stageDir) { Remove-Item -Path $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    Write-Host '==> Publishing framework-dependent win-x64 build'
    dotnet publish src/Oriole.Desktop/Oriole.Desktop.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        -p:Version=$Version `
        --output $stageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # --- Add documentation ---------------------------------------------------
    Copy-Item -Path (Join-Path $repoRoot 'README.md') `
        -Destination (Join-Path $stageDir 'README.md') -Force

    $notes = @"
Oriole $Version - portable win-x64 build

Oriole is a clean-room Windows 11 wallpaper manager. Extract this folder and run
Oriole.exe. This is a framework-dependent build: the .NET 10 Desktop Runtime must
be installed on the target PC.

This package includes no installer and is not code-signed. It contains no API keys
or secrets. Wallhaven images remain the property of their original owners.
"@
    Set-Content -Path (Join-Path $stageDir 'RELEASE-NOTES.txt') -Value $notes -Encoding UTF8

    # --- Zip and hash --------------------------------------------------------
    $zipPath = Join-Path $artifactsDir "oriole-$safeVersion-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force

    $hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash

    Remove-Item -Path $stageDir -Recurse -Force

    Write-Host ''
    Write-Host 'Release package ready.'
    Write-Host "Path:    $zipPath"
    Write-Host "SHA-256: $hash"
}
finally {
    Pop-Location
}
