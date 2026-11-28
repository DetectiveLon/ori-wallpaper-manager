param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    # -Publish delegates to scripts/publish.ps1, the single source of truth for
    # release flags (self-contained, single-file, win-x64) so the two paths can
    # never diverge. That script runs the tests itself via -SkipTests=false.
    if ($Publish) {
        if ($Configuration -ne 'Release') {
            throw '-Publish only supports the Release configuration.'
        }
        & (Join-Path $root 'scripts/publish.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        return
    }

    dotnet restore Ori.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    dotnet test tests/Ori.Core.Tests/Ori.Core.Tests.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

    dotnet build src/Ori.Desktop/Ori.Desktop.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
}
finally {
    Pop-Location
}
