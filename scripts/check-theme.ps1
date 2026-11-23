<#
.SYNOPSIS
    Static theme-regression audit for the Oriole WPF shell.

.DESCRIPTION
    Scans every *.xaml under src/Oriole.Desktop and flags controls that:
      * set Background / Foreground / BorderBrush to a literal white or a
        SystemColors brush, or
      * are IsEnabled="False" without an explicit Style (the WPF default
        disabled template is what originally turned controls white).

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

$xamlRoot        = Join-Path $Root 'src' 'Oriole.Desktop'
$targetElements  = @('CheckBox', 'TextBox', 'PasswordBox', 'ListBox', 'ComboBox', 'Button', 'ScrollBar')
$brushProperties = @('Background', 'Foreground', 'BorderBrush')
$presentationNs  = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'

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
            if ($targetElements -notcontains $elementName) { continue }
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

            foreach ($prop in $brushProperties) {
                if (-not $attr.ContainsKey($prop)) { continue }
                $value = $attr[$prop].Trim()

                $isWhite = (($value -match '(?i)^#(?:FFF|FFFFFF|FFFFFFFF)$') -or
                            ($value -match '(?i)\bWhite\b'))
                $isSystemColors = ($value -match '(?i)SystemColors')

                if ($isWhite -or $isSystemColors) {
                    $kind = if ($isSystemColors) { 'SystemColors' } else { 'literal-white' }
                    [void]$findings.Add(('{0}:{1}  {2}.{3}="{4}"  [{5}]' -f
                        $relPath, $line, $elementName, $prop, $value, $kind))
                }
            }

            if ($attr.ContainsKey('IsEnabled') -and
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

Write-Host 'Oriole theme regression check'
Write-Host ('Scanned {0} XAML file(s) under {1}' -f $filesScanned, $xamlRoot)

if ($findings.Count -eq 0) {
    Write-Host 'PASS: no theme regression findings.'
    exit 0
}

Write-Host ('Findings ({0}):' -f $findings.Count)
foreach ($finding in $findings) { Write-Host ('  ' + $finding) }
Write-Host ('FAIL: {0} theme regression finding(s).' -f $findings.Count)
exit 1
