# Prefer a portable project SDK when present; otherwise use the installed SDK.
$projectRoot = Split-Path -Parent $PSScriptRoot
$portableRoot = Join-Path $projectRoot '.zcode/dotnet10'
if (Test-Path -LiteralPath (Join-Path $portableRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $portableRoot
    $env:PATH = "$portableRoot;$env:PATH"
}
