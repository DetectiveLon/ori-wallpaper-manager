param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    dotnet restore Oriole.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    dotnet test tests/Oriole.Core.Tests/Oriole.Core.Tests.csproj --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

    if ($Publish) {
        dotnet publish src/Oriole.Desktop/Oriole.Desktop.csproj --configuration $Configuration --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
    } else {
        dotnet build src/Oriole.Desktop/Oriole.Desktop.csproj --configuration $Configuration --no-restore
    }
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
}
finally {
    Pop-Location
}
