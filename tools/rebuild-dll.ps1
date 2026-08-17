# Rebuild PioneerFrameworkLite.dll for release/commit.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1
#   powershell -ExecutionPolicy Bypass -File tools/rebuild-dll.ps1 -Configuration Debug
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build "Source/PioneerFramework/PioneerFramework.csproj" --configuration $Configuration -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
    Write-Host "PioneerFrameworkLite.dll rebuilt ($Configuration) -> 1.6/Assemblies/PioneerFrameworkLite.dll"
}
finally {
    Pop-Location
}
