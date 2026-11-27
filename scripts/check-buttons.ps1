<#
.SYNOPSIS
    Static dead-control audit for the Ori WPF shell.

.DESCRIPTION
    Scans every *.xaml under src/Ori.Desktop and flags:
      * interactive elements (Button, ComboBox, CheckBox, ListBox, ToggleButton,
        Hyperlink, RadioButton) that have neither a Command attribute nor a
        Click / SelectionChanged / Checked / Unchecked / RequestNavigate handler,
      * elements with an *.InputBindings property (for example
        Border.InputBindings) whose MouseBinding/KeyBinding has no Command, and
      * any element with a literal IsEnabled="False" (a control the user cannot
        click at all, as opposed to a binding such as IsEnabled="{Binding ...}").

    This is an attribute-level guard only. It does not resolve Binding targets,
    verify command implementations, or detect handlers that exist in code-behind
    but are not wired in XAML. Binding-only controls (SelectedItem / IsChecked /
    ItemsSource / Text) are legitimate: they are driven by a two-way binding
    instead of a command. The known-good ones are listed in $bindingOnlyAllowlist
    below and documented in docs/BUG-INVENTORY.md ("Binding-only allowlist").
    Bindings to a TemplatedParent are control-template plumbing, not app state,
    and are not allowlisted.

    Allowlist keys include the attribute name as well as the binding path, so a
    control that reuses a familiar property path in a different attribute is
    reported instead of being silently allowlisted. Line numbers are
    deliberately not part of the keys, so harmless XAML reflow does not create
    false findings.

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
$targetElements = @('Button', 'ComboBox', 'CheckBox', 'ListBox', 'ToggleButton', 'Hyperlink', 'RadioButton')
$handlerAttrs   = @('Click', 'SelectionChanged', 'Checked', 'Unchecked', 'RequestNavigate')
$presentationNs = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'

# Binding-only controls confirmed wired in docs/BUG-INVENTORY.md. Key format:
#   '<relative xaml path>|<element local name>|<attribute name>|<Binding Path>'
# Paths use '/' so the same list works on Windows and Linux. Several entries may
# share a key (e.g. two "PauseWhenFullscreen" check boxes on different screens).
$bindingOnlyAllowlist = @(
    'src/Ori.Desktop/Views/AutomationView.xaml|CheckBox|IsChecked|PauseWhenFullscreen',
    'src/Ori.Desktop/Views/AutomationView.xaml|CheckBox|IsChecked|AutoFetchFromWallhaven',
    'src/Ori.Desktop/Views/AutomationView.xaml|CheckBox|IsChecked|IsEnabled',
    'src/Ori.Desktop/Views/AutomationView.xaml|ComboBox|SelectedItem|CadenceMinutes',
    'src/Ori.Desktop/Views/AutomationView.xaml|ComboBox|SelectedItem|SelectedInterval',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedItem|SelectedFeedCadence',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedItem|SelectedSorting',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedItem|SelectedTopRange',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedItem|SelectedOrder',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedItem|SelectedRatio',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ComboBox|SelectedItem|SelectedMonitor',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|General',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|Anime',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|People',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|Safe',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|Sketchy',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|Explicit',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ListBox|SelectedItem|SelectedCard',
    'src/Ori.Desktop/Views/DiscoverView.xaml|ToggleButton|IsChecked|IsSelected',
    'src/Ori.Desktop/Views/DisplaysView.xaml|ComboBox|SelectedItem|SelectedPositionOption',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedItem|SelectedCollectionFilter',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedItem|SelectedSort',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|SelectedItem|SelectedDisplay',
    'src/Ori.Desktop/Views/LibraryView.xaml|ComboBox|Text|SelectedCollection',
    'src/Ori.Desktop/Views/LibraryView.xaml|CheckBox|IsChecked|FavoritesOnly',
    'src/Ori.Desktop/Views/LibraryView.xaml|CheckBox|IsChecked|DeleteFileOnRemove',
    'src/Ori.Desktop/Views/LibraryView.xaml|ListBox|SelectedItem|SelectedEntry'
)
$allowlist = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $bindingOnlyAllowlist) { [void]$allowlist.Add($entry) }

function Test-BindingOnly([string]$relativePath, [string]$elementName, [System.Collections.IDictionary]$attributes) {
    foreach ($key in $attributes.Keys) {
        $value = [string]$attributes[$key]
        # TemplatedParent bindings wire control-template parts, not view state.
        if ($value -match 'TemplatedParent') { continue }
        foreach ($match in [regex]::Matches($value, '\{\s*Binding\s+([A-Za-z_][A-Za-z0-9_.]*)')) {
            $candidate = '{0}|{1}|{2}|{3}' -f $relativePath, $elementName, $key, $match.Groups[1].Value
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
        $inInputBindings = $false
        $inputBindingsDepth = -1
        $inputBindingsLine = 0
        $inputBindingsAttributes = @{}

        while ($reader.Read()) {
            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                # An *.InputBindings property element is a click/key target even
                # though the owner element itself has no Click attribute.
                if ($reader.Name -match '^(?:[A-Za-z_][A-Za-z0-9_]*\.)?InputBindings$' -or
                    $reader.LocalName -eq 'InputBindings') {
                    $inInputBindings = $true
                    $inputBindingsDepth = $reader.Depth
                    $lineInfo = [System.Xml.IXmlLineInfo]$reader
                    $inputBindingsLine = if ($null -ne $lineInfo) { $lineInfo.LineNumber } else { 0 }
                    $inputBindingsAttributes = @{}
                    continue
                }

                $elementName = $reader.LocalName
                if ($inInputBindings) {
                    if ($reader.HasAttributes) {
                        for ($i = 0; $i -lt $reader.AttributeCount; $i++) {
                            [void]$reader.MoveToAttribute($i)
                            if ($reader.LocalName -eq 'Command') {
                                $inputBindingsAttributes['Command'] = $reader.Value
                            }
                            elseif ($reader.LocalName -eq 'CommandParameter') {
                                $inputBindingsAttributes['CommandParameter'] = $reader.Value
                            }
                        }
                        [void]$reader.MoveToElement()
                    }
                    continue
                }

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
                    # Template parts (PART_DropDownToggle, expander Header) are
                    # wired by the control template contract, not by view state.
                    $hasTemplateBinding = $false
                    foreach ($key in $attr.Keys) {
                        if ([string]$attr[$key] -match '(?:TemplateBinding|TemplatedParent)') {
                            $hasTemplateBinding = $true
                            break
                        }
                    }

                    if (-not $hasCommand -and -not $hasHandler -and -not $hasTemplateBinding) {
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
            elseif ($reader.NodeType -eq [System.Xml.XmlNodeType]::EndElement -and
                    $inInputBindings -and $reader.Depth -le $inputBindingsDepth) {
                $relPath = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
                $relKey  = $relPath.Replace('\', '/')
                if (-not $inputBindingsAttributes.ContainsKey('Command')) {
                    [void]$findings.Add(('{0}:{1}  InputBindings  [no-command]' -f
                        $relPath, $inputBindingsLine))
                }
                elseif (Test-BindingOnly $relKey 'InputBindings' $inputBindingsAttributes) {
                    $allowedControls++
                }
                $inInputBindings = $false
                $inputBindingsDepth = -1
                $inputBindingsAttributes = @{}
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
