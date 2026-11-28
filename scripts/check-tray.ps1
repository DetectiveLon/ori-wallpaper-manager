# Ori tray click regression guard.
#
# WinForms NotifyIcon raises Click/DoubleClick for RIGHT and MIDDLE clicks too:
# WndProc forwards every WM_*BUTTONUP to WmMouseUp(button), which raises Click
# with that button. Restoring the window from plain Click therefore makes the
# app impossible to keep hidden (right-click reopens it). The service must use
# MouseClick/MouseDoubleClick and gate on MouseButtons.Left. Each subscription's
# handler body is resolved by name (lambda or named method) and inspected
# individually, so one gated handler cannot hide an ungated second one.
param([switch]$Strict)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$file = Join-Path $root 'src\Ori.Desktop\Services\TrayIconService.cs'
if (-not (Test-Path $file)) { throw "Missing $file" }

$text = Get-Content -Raw -LiteralPath $file
$findings = @()

function Get-BalancedBodyFrom([string]$source, [int]$open) {
    $depth = 0
    for ($i = $open; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ }
        elseif ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $source.Substring($open, $i - $open + 1) }
        }
    }
    return $null
}

function Get-HandlerBody([string]$source, [string]$eventName) {
    $pattern = '_notifyIcon\.' + [regex]::Escape($eventName) + '\s*\+=\s*([^;\r\n]+)'
    $subscription = [regex]::Match($source, $pattern)
    if (-not $subscription.Success) { return $null }

    $target = $subscription.Groups[1].Value.Trim()
    if ($target -match '=>') {
        # Lambda handler: locate the first brace after the arrow.
        $arrow = $subscription.Groups[1].Index + $target.IndexOf('=>') + 2
        $open = $source.IndexOf('{', $arrow)
        if ($open -ge 0) { return Get-BalancedBodyFrom $source $open }
        $end = $source.IndexOfAny(@("`r", "`n"), $arrow)
        if ($end -lt 0) { $end = $source.Length }
        return $source.Substring($arrow, $end - $arrow)
    }

    # Named method: resolve the declaration by name instead of grabbing the
    # first brace after the subscription text.
    foreach ($identifier in [regex]::Matches($target, '[A-Za-z_][A-Za-z0-9_]*')) {
        $name = $identifier.Value
        if ($name -in @('new', 'EventHandler', 'sender', 'e', 'handler')) { continue }
        $method = [regex]::Match(
            $source,
            '(?:private|public|protected|internal|static|async|sealed|partial|\s)*\b(?:void|Task)\s+' +
                [regex]::Escape($name) + '\s*\(')
        if (-not $method.Success) { continue }
        $open = $source.IndexOf('{', $method.Index + $method.Length)
        if ($open -ge 0) { return Get-BalancedBodyFrom $source $open }
    }
    return $null
}

if ($text -match '_notifyIcon\.Click\s*\+=') {
    $findings += 'plain Click subscription restores on right/middle click'
}
if ($text -match '_notifyIcon\.DoubleClick\s*\+=') {
    $findings += 'plain DoubleClick subscription restores on right/middle double-click'
}

foreach ($eventName in @('MouseClick', 'MouseDoubleClick')) {
    $body = Get-HandlerBody $text $eventName
    if (-not $body) {
        $findings += "missing or unresolvable $eventName handler body"
        continue
    }
    if ($body -notmatch 'MouseButtons\.Left') {
        $findings += "$eventName handler body does not gate on MouseButtons.Left"
    }
    if ($body -notmatch '_onOpen') {
        $findings += "$eventName handler body does not call the open callback"
    }
}

# Tray notifications (rotation/failure feedback) must be exposed via a Notify
# helper that uses a balloon tip, not via plain Click handlers.
if ($text -notmatch '\bNotify\s*\(') {
    $findings += 'missing Notify(...) helper for tray notifications'
}
$notifyPattern = '(?:private|public|protected|internal|static|async|sealed|partial|\s)*\bvoid\s+Notify\s*\('
$notify = [regex]::Match($text, $notifyPattern)
if (-not $notify.Success) {
    $findings += 'missing Notify(...) method declaration'
}
else {
    $notifyOpen = $text.IndexOf('{', $notify.Index + $notify.Length)
    $body = if ($notifyOpen -ge 0) { Get-BalancedBodyFrom $text $notifyOpen } else { $null }
    if (-not $body -or $body -notmatch 'ShowBalloonTip') {
        $findings += 'tray Notify(...) does not raise a balloon tip'
    }
}

Write-Host 'Ori tray click guard'
if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Host "  FAIL: $_" }
    Write-Host 'FAIL: tray restore is not gated to left clicks.'
    if ($Strict) { exit 1 }
    exit 0
}
Write-Host 'PASS: tray restore is gated to left clicks only.'
