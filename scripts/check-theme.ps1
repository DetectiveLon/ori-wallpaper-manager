<#
.SYNOPSIS
    Static theme-regression audit for the Ori WPF shell.

.DESCRIPTION
    Scans every *.xaml under src/Ori.Desktop and flags:
      * any element whose brush-like attribute (Background, Foreground,
        BorderBrush, Fill, Stroke, Color, *Brush) is a literal white or a
        SystemColors brush — including SolidColorBrush Color in App.xaml and
        brushes on Border / Popup / Window / Expander, and
      * controls that are IsEnabled="False" without an explicit Style (the WPF
        default disabled template is what originally turned controls white).

    Intentional resources are exempt only through the explicit $brushAllowlist
    below, keyed by relative XAML path, element, attribute and value. No
    intentional white brush ships today, so the list is empty; every entry must
    be reviewed alongside docs/QA-SMOKE.md before it is added.

    Prints a compact file:line:element report and exits 1 when anything is
    found, 0 when clean. Uses only PowerShell built-ins and System.Xml; no
    external modules and no build/test of the application.

.EXAMPLE
    pwsh -NoProfile -File scripts/check-theme.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = (Get-Location).Path }

$xamlRoot        = Join-Path $Root 'src' 'Ori.Desktop'
$brushProperties = @('Background', 'Foreground', 'BorderBrush', 'Fill', 'Stroke', 'Color')
$disabledTargets = @('CheckBox', 'TextBox', 'PasswordBox', 'ListBox', 'ComboBox', 'Button', 'ScrollBar')
$presentationNs  = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'

# Format: '<relative xaml path>|<element local name>|<attribute name>|<value>'.
# Empty by design: no intentional white/SystemColors brush exists in the shell.
$brushAllowlist = @()
$allowlist = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $brushAllowlist) { [void]$allowlist.Add($entry) }

$findings      = New-Object System.Collections.Generic.List[string]
$filesScanned  = 0

if (-not (Test-Path -LiteralPath $xamlRoot -PathType Container)) {
    Write-Host "Theme audit: XAML root not found: $xamlRoot"
    exit 2
}

$files = @(Get-ChildItem -LiteralPath $xamlRoot -Recurse -File -Filter '*.xaml' |
           Sort-Object -Property FullName)

foreach ($file in $files) {
    $filesScanned++
    $raw = Get-Content -LiteralPath $file.FullName -Raw
    $reader = $null

    try {
        $reader = [System.Xml.XmlReader]::Create([System.IO.StringReader]::new($raw))

        while ($reader.Read()) {
            if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }

            $elementName = $reader.LocalName
            # Ignore look-alike elements from non-presentation namespaces.
            if ($reader.NamespaceURI -and $reader.NamespaceURI -ne $presentationNs) { continue }

            $line = 0
            $lineInfo = [System.Xml.IXmlLineInfo]$reader
            if ($null -ne $lineInfo) { $line = $lineInfo.LineNumber }

            $attr = @{}
            if ($reader.HasAttributes) {
                for ($i = 0; $i -lt $reader.AttributeCount; $i++) {
                    [void]$reader.MoveToAttribute($i)
                    $attr[$reader.LocalName] = $reader.Value
                }
                [void]$reader.MoveToElement()
            }

            $relPath = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
            $relKey  = $relPath.Replace('\', '/')

            foreach ($key in $attr.Keys) {
                $value = [string]$attr[$key]
                $isBrushAttribute = ($brushProperties -contains $key) -or
                                    ($key -match '(?i)(?:Brush|Background|Foreground|Fill|Stroke|Color)$') -or
                                    ($key -eq 'Value' -and $attr.ContainsKey('Property') -and
                                     ([string]$attr['Property']) -match '(?i)(?:Brush|Background|Foreground|Fill|Stroke)$')
                if (-not $isBrushAttribute) { continue }

                $trimmed = $value.Trim()
                $isWhite = (($trimmed -match '(?i)^#(?:FFFF|FFFFFF|FFFFFFFF)$') -or
                            ($trimmed -match '(?i)\bWhite\b'))
                $isSystemColors = ($trimmed -match '(?i)SystemColors')

                if (-not ($isWhite -or $isSystemColors)) { continue }

                if ($allowlist.Contains(('{0}|{1}|{2}|{3}' -f $relKey, $elementName, $key, $trimmed))) {
                    continue
                }

                $kind = if ($isSystemColors) { 'SystemColors' } else { 'literal-white' }
                [void]$findings.Add(('{0}:{1}  {2}.{3}="{4}"  [{5}]' -f
                    $relPath, $line, $elementName, $key, $trimmed, $kind))
            }

            if ($disabledTargets -contains $elementName -and
                $attr.ContainsKey('IsEnabled') -and
                ($attr['IsEnabled'] -match '^(?i:false)$') -and
                (-not $attr.ContainsKey('Style'))) {
                [void]$findings.Add(('{0}:{1}  {2} IsEnabled=False without explicit Style  [disabled-no-style]' -f
                    $relPath, $line, $elementName))
            }
        }
    }
    catch {
        [void]$findings.Add(('{0}:0  XML_PARSE_ERROR: {1}' -f $file.Name, $_.Exception.Message))
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
    }
}

Write-Host 'Ori theme regression check'
Write-Host ('Scanned {0} XAML file(s) under {1}' -f $filesScanned, $xamlRoot)

if ($findings.Count -eq 0) {
    Write-Host 'PASS: no theme regression findings.'
    exit 0
}

Write-Host ('Findings ({0}):' -f $findings.Count)
foreach ($finding in $findings) { Write-Host ('  ' + $finding) }
Write-Host ('FAIL: {0} theme regression finding(s).' -f $findings.Count)
exit 1