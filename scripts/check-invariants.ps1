<#
.SYNOPSIS
Fast source-level guards for the v2-shell release/packaging invariants.

.DESCRIPTION
Checks, without building or launching anything, that:
  * the SDK is pinned exactly (global.json 10.0.404, no floating 10.0.x in CI),
  * no hardcoded 0.1.0 version survives in the owned packaging/docs files,
  * no hardcoded app version survives in SettingsViewModel.cs / WallhavenClient.cs,
  * csproj <Version> and app.manifest assemblyIdentity version agree,
  * rotation feed persistence, library import + dedupe methods exist,
  * each tray MouseClick/MouseDoubleClick handler is resolved by its target
    (lambda or named method) and THAT body gates on MouseButtons.Left and calls
    the open callback (not just a MouseButtons.Left token somewhere in the file),
    Notify shows a balloon tip, and no plain _notifyIcon.Click survives,
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

# Returns the balanced { ... } body that starts at $open, or $null.
function Get-BalancedBodyFrom([string]$text, [int]$open) {
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($i = $open; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $text.Substring($open, $i - $open + 1) }
        }
    }
    return $null
}

# Resolves the actual target of an event subscription: a lambda body, or the
# named method's declaration. Taking the first '{' after the subscription text
# can inspect an unrelated method when the handler is a method group.
function Get-SubscriptionBody([string]$text, [string]$eventName) {
    $pattern = '_notifyIcon\.' + [regex]::Escape($eventName) + '\s*\+=\s*([^;\r\n]+)'
    $subscription = [regex]::Match($text, $pattern)
    if (-not $subscription.Success) { return $null }

    $target = $subscription.Groups[1].Value.Trim()
    if ($target -match '=>') {
        $arrow = $subscription.Groups[1].Index + $target.IndexOf('=>') + 2
        $open = $text.IndexOf('{', $arrow)
        if ($open -ge 0) { return Get-BalancedBodyFrom $text $open }
        $end = $text.IndexOfAny(@("`r", "`n"), $arrow)
        if ($end -lt 0) { $end = $text.Length }
        return $text.Substring($arrow, $end - $arrow)
    }

    foreach ($identifier in [regex]::Matches($target, '[A-Za-z_][A-Za-z0-9_]*')) {
        $name = $identifier.Value
        if ($name -in @('new', 'EventHandler', 'sender', 'e', 'handler')) { continue }
        $method = [regex]::Match(
            $text,
            '(?:private|public|protected|internal|static|async|sealed|partial|\s)*\b(?:void|Task)\s+' +
                [regex]::Escape($name) + '\s*\(')
        if (-not $method.Success) { continue }
        $open = $text.IndexOf('{', $method.Index + $method.Length)
        if ($open -ge 0) { return Get-BalancedBodyFrom $text $open }
    }
    return $null
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
    'README.md', 'src/Ori.Desktop/Ori.Desktop.csproj',
    'src/Ori.Desktop/app.manifest', 'docs/RELEASING.md',
    'docs/IMPLEMENTATION_STATUS.md', 'scripts/publish.ps1'
)
foreach ($rel in $versionOwned) {
    $text = Read-Text $rel
    if ($text -match '0\.1\.0') { Add-Finding "$rel contains a hardcoded 0.1.0 version." }
}

# 2b. The app version must come from assembly metadata, not a literal in code.
#     Ignore comment lines (they may legitimately show "1.2.3+sha" examples) and
#     arithmetic decimals such as 1024.0. Three-part versions and the legacy
#     two-part "0.1" user-agent token are the forms that actually shipped.
$versionLiteralPattern = '(?i)(?:\bv?\d+\.\d+\.\d+(?:\+[0-9A-Za-z.]+)?|\b0\.1\b)'
foreach ($rel in @(
    'src/Ori.Desktop/ViewModels/SettingsViewModel.cs',
    'src/Ori.Core/Services/WallhavenClient.cs'
)) {
    $lines = (Read-Text $rel) -split "\r?\n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].TrimStart()
        if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('*')) { continue }
        if ($lines[$i] -match $versionLiteralPattern) {
            Add-Finding ("{0}:{1} contains a hardcoded version string; read it from assembly metadata." -f
                $rel, ($i + 1))
        }
    }
}

# 3. csproj <Version> and app.manifest assemblyIdentity version agree.
$csproj = Read-Text 'src/Ori.Desktop/Ori.Desktop.csproj'
$manifest = Read-Text 'src/Ori.Desktop/app.manifest'
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
$feed = Read-Text 'src/Ori.Core/Services/WallhavenFeedStore.cs'
if ($feed -and $feed -notmatch 'class\s+WallhavenFeedStore') {
    Add-Finding 'Rotation feed store class not found in WallhavenFeedStore.cs.'
}
$library = Read-Text 'src/Ori.Core/Services/WallpaperLibraryStore.cs'
if ($library) {
    if ($library -notmatch 'ImportFolderAsync') { Add-Finding 'Library import method (ImportFolderAsync) not found.' }
    if ($library -notmatch 'FindDuplicatesAsync') { Add-Finding 'Library dedupe method (FindDuplicatesAsync) not found.' }
}

# 5. Tray: inspect handler bodies, not just method-name tokens. WinForms raises
#    plain Click/DoubleClick for right and middle clicks too, so each Mouse*
#    handler must itself check e.Button before calling the open callback.
$tray = Read-Text 'src/Ori.Desktop/Services/TrayIconService.cs'
if ($tray) {
    if ($tray -match '_notifyIcon\.Click\s*\+=') { Add-Finding 'Plain _notifyIcon.Click subscription must not be used.' }
    if ($tray -match '_notifyIcon\.DoubleClick\s*\+=') { Add-Finding 'Plain _notifyIcon.DoubleClick subscription must not be used.' }

    foreach ($eventName in @('MouseClick', 'MouseDoubleClick')) {
        $body = Get-SubscriptionBody $tray $eventName
        if (-not $body) {
            Add-Finding ("Tray '{0}' subscription has no resolvable handler body." -f $eventName)
            continue
        }
        if ($body -notmatch 'MouseButtons\.Left') {
            Add-Finding ("Tray {0} handler body does not gate on MouseButtons.Left." -f $eventName)
        }
        if ($body -notmatch '_onOpen') {
            Add-Finding ("Tray {0} handler body does not call the open callback." -f $eventName)
        }
    }

    $notifyMethod = [regex]::Match($tray, '\bvoid\s+Notify\s*\(')
    $notifyBody = if ($notifyMethod.Success) {
        Get-BalancedBodyFrom $tray $tray.IndexOf('{', $notifyMethod.Index + $notifyMethod.Length)
    }
    else { $null }
    if (-not $notifyBody) {
        Add-Finding 'Tray Notify(...) method has no parseable body.'
    }
    elseif ($notifyBody -notmatch 'ShowBalloonTip') {
        Add-Finding 'Tray Notify(...) body does not raise a balloon tip.'
    }
}

# 6. Editable collection ComboBox exists in the library view.
$libraryView = Read-Text 'src/Ori.Desktop/Views/LibraryView.xaml'
if ($libraryView -and $libraryView -notmatch 'IsEditable\s*=\s*"True"') {
    Add-Finding 'Editable collection ComboBox (IsEditable="True") not found in LibraryView.xaml.'
}

Write-Host 'Ori v2-shell invariant check'
if ($findings.Count -eq 0) {
    Write-Host 'PASS: all v2-shell invariants hold.'
    exit 0
}
Write-Host ("Findings ({0}):" -f $findings.Count)
foreach ($finding in $findings) { Write-Host ('  FAIL: ' + $finding) }
Write-Host ("FAIL: {0} invariant finding(s)." -f $findings.Count)
exit 1
