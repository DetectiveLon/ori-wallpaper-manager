# Ori tray click regression guard.
#
# WinForms NotifyIcon raises Click/DoubleClick for RIGHT and MIDDLE clicks too:
# WndProc forwards every WM_*BUTTONUP to WmMouseUp(button), which raises Click
# with that button. Restoring the window from plain Click therefore makes the
# app impossible to keep hidden (right-click reopens it). The service must use
# MouseClick/MouseDoubleClick and gate on MouseButtons.Left.
param([switch]$Strict)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$file = Join-Path $root 'src\Ori.Desktop\Services\TrayIconService.cs'
if (-not (Test-Path $file)) { throw "Missing $file" }

$text = Get-Content -Raw -LiteralPath $file
$findings = @()

if ($text -match '_notifyIcon\.Click\s*\+=') {
    $findings += 'plain Click subscription restores on right/middle click'
}
if ($text -match '_notifyIcon\.DoubleClick\s*\+=') {
    $findings += 'plain DoubleClick subscription restores on right/middle double-click'
}
if ($text -notmatch '_notifyIcon\.MouseClick\s*\+=') {
    $findings += 'missing MouseClick subscription for left-click restore'
}
if ($text -notmatch '_notifyIcon\.MouseDoubleClick\s*\+=') {
    $findings += 'missing MouseDoubleClick subscription for double-click restore'
}
if ($text -notmatch 'MouseButtons\.Left') {
    $findings += 'missing MouseButtons.Left guard'
}
# Tray notifications (rotation/failure feedback) must be exposed via a Notify
# helper that uses a balloon tip, not via plain Click handlers.
if ($text -notmatch '\bNotify\s*\(') {
    $findings += 'missing Notify(...) helper for tray notifications'
}
if ($text -notmatch 'ShowBalloonTip') {
    $findings += 'tray Notify(...) does not raise a balloon tip'
}

Write-Host 'Ori tray click guard'
if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Host "  FAIL: $_" }
    Write-Host 'FAIL: tray restore is not gated to left clicks.'
    if ($Strict) { exit 1 }
    exit 0
}
Write-Host 'PASS: tray restore is gated to left clicks only.'
