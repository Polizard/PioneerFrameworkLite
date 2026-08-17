# Check whether committed 1.6/Assemblies/PioneerFrameworkLite.dll matches a fresh Release build.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/check-committed-dll.ps1
#   powershell -ExecutionPolicy Bypass -File tools/check-committed-dll.ps1 -Configuration Debug
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $env:TEMP "pfl_check"
$committedDll = Join-Path $root "1.6/Assemblies/PioneerFrameworkLite.dll"

Push-Location $root
try {
    Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
    dotnet build "Source/PioneerFramework/PioneerFramework.csproj" --configuration $Configuration -v:minimal -p:OutputPath=$out -p:AppendTargetFrameworkToOutputPath=false
    if ($LASTEXITCODE -ne 0) { throw "build failed" }

    $committedHash = (Get-FileHash $committedDll).Hash
    $builtHash = (Get-FileHash "$out/PioneerFrameworkLite.dll").Hash
    if ($committedHash -ne $builtHash) {
        Write-Host "Committed DLL differs from $Configuration build."
        Write-Host "committed=$committedHash"
        Write-Host "built    =$builtHash"
        exit 1
    }
    Write-Host "Committed DLL matches $Configuration build: $committedHash"
}
finally {
    Pop-Location
}
