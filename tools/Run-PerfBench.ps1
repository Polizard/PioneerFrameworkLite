# Run the celestial benchmark suite and compare against the recorded baseline.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/Run-PerfBench.ps1
#   powershell -ExecutionPolicy Bypass -File tools/Run-PerfBench.ps1 -Tolerance 0.25
#   powershell -ExecutionPolicy Bypass -File tools/Run-PerfBench.ps1 -UpdateBaseline
#
# Scenarios: step / ephemeris / frames / ships / bodies (light first, heavy last).
# Baseline:  tools/CelestialBenchmark/perf-baseline/<scenario>/metrics.json (committed).
# Exit code: 0 = all gated metrics within tolerance, 1 = regression or bench failure.
#
# Gating policy (2026-11-18, from measured variance on the same commit/machine):
#   step 15% (p50 + allocations; p95 is GC tail -> informational)
#   ephemeris 10%, frames 20%
#   bodies / ships: informational only (same-commit reruns differ by up to 126%).
#   Light scenarios run first: after minutes of bodies/ships the same machine slows
#   frames by ~5x (power/thermal), which used to produce false reds.
#
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).

param(
    [string]$Output = "",
    [double]$Tolerance = 0,
    [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ([string]::IsNullOrWhiteSpace($Output)) {
        $Output = "..\\.zcode\\perf\\" + (Get-Date -Format "yyyyMMdd-HHmmss")
    }
    $Output = [System.IO.Path]::GetFullPath((Join-Path $root $Output))
    New-Item -ItemType Directory -Force -Path $Output | Out-Null
    Write-Host "output: $Output" -ForegroundColor Cyan

    foreach ($scenario in @("step", "ephemeris", "frames", "ships", "bodies")) {
        Write-Host ""
        Write-Host "=== bench: $scenario ===" -ForegroundColor Cyan
        & dotnet run -c Release --project tools/CelestialBenchmark -- --scenario $scenario --output (Join-Path $Output $scenario)
        if ($LASTEXITCODE -ne 0) {
            Write-Host "bench failed: $scenario (exit $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        }
        Start-Sleep -Seconds 3
    }

    $baseline = Join-Path $root "tools\CelestialBenchmark\perf-baseline"
    if ($UpdateBaseline) {
        foreach ($scenario in @("step", "ephemeris", "frames", "ships", "bodies")) {
            $target = Join-Path $baseline $scenario
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item (Join-Path (Join-Path $Output $scenario) "metrics.json") (Join-Path $target "metrics.json") -Force
        }
        Write-Host "baseline updated: $baseline" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "=== compare vs baseline ===" -ForegroundColor Cyan
    & dotnet run -c Release --project tools/CelestialBenchmark -- --scenario compare --a $baseline --b $Output --tolerance $Tolerance
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
