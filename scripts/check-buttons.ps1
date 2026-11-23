<#
.SYNOPSIS
    Static dead-control audit for the Oriole WPF shell.

.DESCRIPTION
    Scans every *.xaml under src/Oriole.Desktop and flags:
      * Button / ComboBox / CheckBox / ListBox elements that have neither a
        Command attribute nor a Click / SelectionChanged / Checked handler, and
      * any element with a literal IsEnabled="False" (a control the user cannot
        click at all, as opposed to a binding such as IsEnabled="{Binding ...}").

    This is an attribute-level guard only. It does not resolve Binding targets,
    verify command implementations, or detect handlers that exist in code-behind
    but are not wired in XAML. Binding-only controls (SelectedItem / IsChecked /
    ItemsSource) are deliberately reported too, because they have no command or
    event handler; docs/BUG-INVENTORY.md records which of those are genuinely
    wired. Use it to triage the findings.

    Prints a compact file:line:element report. Exits 1 only when -Strict is
    supplied and findings exist; otherwise exits 0 and prints a warning count.
    Uses only PowerShell built-ins and System.Xml: no external modules, and no
    build/test/commit/push of the application.

.EXAMPLE
    pwsh -NoProfile -File scripts/check-buttons.ps1
    pwsh -NoProfile -File scripts/check-buttons.ps1 -Strict
    pwsh -NoProfile -File scripts/check-buttons.ps1 -Root C:\repo
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
}

$xamlRoot       = Join-Path (Join-Path $Root 'src') 'Oriole.Desktop'
$targetElements = @('Button', 'ComboBox', 'CheckBox', 'ListBox')
$handlerAttrs   = @('Click', 'SelectionChanged', 'Checked')
$presentationNs = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'

$findings     = New-Object System.Collections.Generic.List[string]
$filesScanned = 0

if (-not (Test-Path -LiteralPath $xamlRoot -PathType Container)) {
    Write-Host "Dead-control audit: XAML root not found: $xamlRoot"
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

            if ($targetElements -contains $elementName) {
                $hasCommand = $attr.ContainsKey('Command')
                $hasHandler = $false
                foreach ($name in $handlerAttrs) {
                    if ($attr.ContainsKey($name)) { $hasHandler = $true; break }
                }

                if (-not $hasCommand -and -not $hasHandler) {
                    [void]$findings.Add(('{0}:{1}  {2}  [no-command-no-handler]' -f
                        $relPath, $line, $elementName))
                }
            }

            if ($attr.ContainsKey('IsEnabled') -and
                ($attr['IsEnabled'] -match '^(?i:false)$')) {
                [void]$findings.Add(('{0}:{1}  {2} IsEnabled=False  [literal-disabled]' -f
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

Write-Host 'Oriole dead-control check'
Write-Host ('Scanned {0} XAML file(s) under {1}' -f $filesScanned, $xamlRoot)

if ($findings.Count -eq 0) {
    Write-Host 'PASS: no unbound controls or literal-disabled controls found.'
    exit 0
}

Write-Host ('Findings ({0}):' -f $findings.Count)
foreach ($finding in $findings) { Write-Host ('  ' + $finding) }

if ($Strict) {
    Write-Host ('FAIL: {0} dead-control finding(s) (-Strict).' -f $findings.Count)
    exit 1
}

Write-Host ('WARN: {0} dead-control finding(s); warning only (use -Strict to fail).' -f $findings.Count)
exit 0
