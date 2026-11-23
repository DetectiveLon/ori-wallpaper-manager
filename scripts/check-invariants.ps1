<#
.SYNOPSIS
Fast source-level guards for the v2-shell release/packaging invariants.

.DESCRIPTION
Checks, without building or launching anything, that:
  * the SDK is pinned exactly (global.json 10.0.404, no floating 10.0.x in CI),
  * no hardcoded 0.1.0 version survives in the owned packaging/docs files,
  * csproj <Version> and app.manifest assemblyIdentity version agree,
  * rotation feed persistence, library import + dedupe methods exist,
  * tray Notify/balloon support exists and no plain _notifyIcon.Click survives,
  * the editable collection ComboBox exists in the library view.

PowerShell built-ins only; no modules, no build, no network, no system changes.
Exit 0 when clean, 1 on any finding.
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = (Get-Location).Path }

$findings = New-Object System.Collections.Generic.List[string]
function Add-Finding([string]$message) { [void]$findings.Add($message) }
function Read-Text([string]$relative) {
    $path = Join-Path $Root $relative
    if (-not (Test-Path -LiteralPath $path)) { Add-Finding "Missing file: $relative"; return '' }
    return Get-Content -LiteralPath $path -Raw
}

# 1. Exact SDK pin, no floating 10.0.x in CI/release.
$globalJson = Read-Text 'global.json'
if ($globalJson) {
    if ($globalJson -notmatch '"version"\s*:\s*"10\.0\.404"') {
        Add-Finding 'global.json must pin SDK version 10.0.404.'
    }
    if ($globalJson -notmatch '"rollForward"\s*:\s*"disable"') {
        Add-Finding 'global.json must set rollForward=disable (exact SDK).'
    }
}
foreach ($wf in @('.github/workflows/ci.yml', '.github/workflows/release.yml')) {
    $text = Read-Text $wf
    if ($text -match '10\.0\.x') { Add-Finding "$wf still uses floating 10.0.x." }
    if ($text -notmatch '10\.0\.404') { Add-Finding "$wf must pin dotnet-version 10.0.404." }
}

# 2. No hardcoded 0.1.0 version in the owned packaging/docs files.
$versionOwned = @(
    'README.md', 'src/Oriole.Desktop/Oriole.Desktop.csproj',
    'src/Oriole.Desktop/app.manifest', 'docs/RELEASING.md',
    'docs/IMPLEMENTATION_STATUS.md', 'scripts/publish.ps1'
)
foreach ($rel in $versionOwned) {
    $text = Read-Text $rel
    if ($text -match '0\.1\.0') { Add-Finding "$rel contains a hardcoded 0.1.0 version." }
}

# 3. csproj <Version> and app.manifest assemblyIdentity version agree.
$csproj = Read-Text 'src/Oriole.Desktop/Oriole.Desktop.csproj'
$manifest = Read-Text 'src/Oriole.Desktop/app.manifest'
$projVersion = ''
if ($csproj -match '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>') { $projVersion = $Matches[1] }
$manifestVersion = ''
if ($manifest -match '<assemblyIdentity[^>]*version="([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)"') { $manifestVersion = $Matches[1] }
if ($projVersion -and $manifestVersion) {
    $expected = "$projVersion.0"
    if ($manifestVersion -ne $expected) {
        Add-Finding "Version mismatch: csproj $projVersion vs app.manifest $manifestVersion (expected $expected)."
    }
}
else {
    Add-Finding 'Could not parse the csproj Version or app.manifest assemblyIdentity version.'
}

# 4. Rotation feed persistence + library import/dedupe methods exist.
$feed = Read-Text 'src/Oriole.Core/Services/WallhavenFeedStore.cs'
if ($feed -and $feed -notmatch 'class\s+WallhavenFeedStore') {
    Add-Finding 'Rotation feed store class not found in WallhavenFeedStore.cs.'
}
$library = Read-Text 'src/Oriole.Core/Services/WallpaperLibraryStore.cs'
if ($library) {
    if ($library -notmatch 'ImportFolderAsync') { Add-Finding 'Library import method (ImportFolderAsync) not found.' }
    if ($library -notmatch 'FindDuplicatesAsync') { Add-Finding 'Library dedupe method (FindDuplicatesAsync) not found.' }
}

# 5. Tray Notify exists and no plain Click subscription survives.
$tray = Read-Text 'src/Oriole.Desktop/Services/TrayIconService.cs'
if ($tray) {
    if ($tray -notmatch '\bNotify\s*\(') { Add-Finding 'Tray Notify(...) method not found.' }
    if ($tray -notmatch 'ShowBalloonTip') { Add-Finding 'Tray balloon-tip notification not found.' }
    if ($tray -match '_notifyIcon\.Click\s*\+=') { Add-Finding 'Plain _notifyIcon.Click subscription must not be used.' }
}

# 6. Editable collection ComboBox exists in the library view.
$libraryView = Read-Text 'src/Oriole.Desktop/Views/LibraryView.xaml'
if ($libraryView -and $libraryView -notmatch 'IsEditable\s*=\s*"True"') {
    Add-Finding 'Editable collection ComboBox (IsEditable="True") not found in LibraryView.xaml.'
}

Write-Host 'Oriole v2-shell invariant check'
if ($findings.Count -eq 0) {
    Write-Host 'PASS: all v2-shell invariants hold.'
    exit 0
}
Write-Host ("Findings ({0}):" -f $findings.Count)
foreach ($finding in $findings) { Write-Host ('  FAIL: ' + $finding) }
Write-Host ("FAIL: {0} invariant finding(s)." -f $findings.Count)
exit 1
