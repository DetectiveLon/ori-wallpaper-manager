<#
.SYNOPSIS
    Static dead-control audit for the Ori WPF shell.

.DESCRIPTION
    Scans every *.xaml under src/Ori.Desktop and flags:
      * Button / ComboBox / CheckBox / ListBox elements that have neither a
        Command attribute nor a Click / SelectionChanged / Checked handler, and
      * any element with a literal IsEnabled="False" (a control the user cannot
        click at all, as opposed to a binding such as IsEnabled="{Binding ...}").

    This is an attribute-level guard only. It does not resolve Binding targets,
    verify command implementations, or detect handlers that exist in code-behind
    but are not wired in XAML. Binding-only controls (SelectedItem / IsChecked /
    ItemsSource / Text) are legitimate: they are driven by a two-way binding
    instead of a command. The known-good ones are listed in $bindingOnlyAllowlist
    below and documented in docs/BUG-INVENTORY.md ("Binding-only allowlist");
    they are not reported. Anything left is a real dead control. Line numbers are
    deliberately not part of the allowlist keys, so harmless XAML reflow does not
    create false findings.

    Prints a compact file:line:element report. Exits 1 only when -Strict is
    supplied and unallowlisted findings exist; otherwise exits 0 and prints a
    warning count. Uses only PowerShell built-ins and System.Xml: no external
    modules, and no build/test/commit/push of the application.

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

$xamlRoot       = Join-Path (Join-Path $Root 'src') 'Ori.Desktop'
$targetElements = @('Button', 'ComboBox', 'CheckBox', 'ListBox')
$handlerAttrs   = @('Click', 'SelectionChanged', 'Checked')
$presentationNs = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'

# Binding-only controls confirmed wired in docs/BUG-INVENTORY.md. Key format:
#   '<relative xaml path>|<element local name>|<one {Binding Path} on that element>'
# Paths use '/' so the same list works on Windows and Linux. Several entries may
# share a key (e.g. two "PauseWhenFullscreen" check boxes on different screens).
$bindingOnlyAllowlist = @(
    'src/Ori.Desktop/Views/AutomationView.xaml|CheckBox|PauseWhenFullscreen',
    'src/Ori.Desktop/Views/AutomationView.xaml|CheckBox|AutoFetchFromWallhaven',
    'src/Ori.Desktop/Views/AutomationView.xaml|CheckBox|IsEnabled',
    'src/Ori.Desktop/Views/AutomationView.xaml|ComboBox|CadenceMinutes',
    'src/Ori.Desktop/Views/AutomationView.xaml|ComboBox|SelectedInterval',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedFeedCadence',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedSorting',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedTopRange',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedOrder',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedRatio',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedMonitor',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ListBox|SelectedCard',
    'src/Ori.Desktop/Views/DisplaysView.xaml|ComboBox|SelectedPositionOption',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedCollectionFilter',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedSort',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedDisplay',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedCollection',
    'src/Ori.Desktop/Views/LibraryView.xaml|CheckBox|FavoritesOnly',
    'src/Ori.Desktop/Views/LibraryView.xaml|CheckBox|DeleteFileOnRemove',
    'src/Ori.Desktop/Views/LibraryView.xaml|ListBox|SelectedEntry'
)
$allowlist = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $bindingOnlyAllowlist) { [void]$allowlist.Add($entry) }

function Test-BindingOnly([string]$relativePath, [string]$elementName, [System.Collections.IDictionary]$attributes) {
    foreach ($key in $attributes.Keys) {
        $value = [string]$attributes[$key]
        foreach ($match in [regex]::Matches($value, '\{\s*Binding\s+([A-Za-z_][A-Za-z0-9_.]*)')) {
            $candidate = '{0}|{1}|{2}' -f $relativePath, $elementName, $match.Groups[1].Value
            if ($allowlist.Contains($candidate)) { return $true }
        }
    }
    return $false
}

$findings        = New-Object System.Collections.Generic.List[string]
$allowedControls = 0
$filesScanned    = 0

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
            $relKey  = $relPath.Replace('\', '/')

            if ($targetElements -contains $elementName) {
                $hasCommand = $attr.ContainsKey('Command')
                $hasHandler = $false
                foreach ($name in $handlerAttrs) {
                    if ($attr.ContainsKey($name)) { $hasHandler = $true; break }
                }

                if (-not $hasCommand -and -not $hasHandler) {
                    if (Test-BindingOnly $relKey $elementName $attr) {
                        $allowedControls++
                    }
                    else {
                        [void]$findings.Add(('{0}:{1}  {2}  [no-command-no-handler]' -f
                            $relPath, $line, $elementName))
                    }
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

Write-Host 'Ori dead-control check'
Write-Host ('Scanned {0} XAML file(s) under {1}' -f $filesScanned, $xamlRoot)
Write-Host ('Binding-only controls allowlisted: {0} (see docs/BUG-INVENTORY.md)' -f $allowedControls)

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
