<#
.SYNOPSIS
Optional: compile the per-user Inno Setup installer for Ori.

.DESCRIPTION
Inno Setup is OPTIONAL and is not installed by this script. If the compiler
(iscc.exe) is not found, the script prints a clear message and exits 2 without
changing anything. It reads the version from artifacts/update-manifest.json
when available, otherwise from -Version, otherwise from Ori.Desktop.csproj.

This script only COMPILES an installer EXE into artifacts/. It never installs
Ori, never touches HKCU Run, and never modifies the real system.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent (Split-Path -Parent $scriptDir)
$iss       = Join-Path $scriptDir 'Ori.iss'
$manifest  = Join-Path $repoRoot 'artifacts/update-manifest.json'
$sourceExe = Join-Path $repoRoot 'artifacts/Ori.exe'

if (-not (Test-Path -LiteralPath $iss)) { throw "Missing installer script: $iss" }
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Missing $sourceExe. Run scripts/publish.ps1 first to produce the single-file EXE."
}

if ([string]::IsNullOrWhiteSpace($Version) -and (Test-Path -LiteralPath $manifest)) {
    $Version = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).version
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src/Ori.Desktop/Ori.Desktop.csproj') -Raw
    $Version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($iscc) {
        $CompilerPath = $iscc.Source
    }
    else {
        $candidates = @(
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        )
        $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath)) {
    Write-Host 'Inno Setup is not installed (optional). Install Inno Setup 6 from https://jrsoftware.org/isinfo.php'
    Write-Host 'then re-run with -CompilerPath "C:\Path\To\ISCC.exe", or pass the path via PATH.'
    exit 2
}

Push-Location $scriptDir
try {
    & $CompilerPath "/DAppVersion=$Version" "/DSourceExe=$sourceExe" $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
    Write-Host "Installer built for Ori $Version under artifacts/."
}
finally {
    Pop-Location
}
