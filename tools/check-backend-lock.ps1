# check-backend-lock.ps1 -- guard for BACKEND-LOCK.md (ASCII-only on purpose:
# Windows PowerShell 5.1 reads BOM-less files as ANSI).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/check-backend-lock.ps1
#   powershell -ExecutionPolicy Bypass -File tools/check-backend-lock.ps1 -Base origin/master
#   powershell -ExecutionPolicy Bypass -File tools/check-backend-lock.ps1 -Fail    # exit 1 when violated
#
# Semantics:
#   * backend paths = physics step / time base / state commit code (see BACKEND-LOCK.md)
#   * if any backend path changed AND no authorization note exists
#       (Source/PioneerFramework/DesignNotes/后端变更授权-*.md)
#     => report it. With -Fail: exit 1.
#   * default mode only REPORTS (does not break anyone's build / CI).

param(
    [string]$Base = "HEAD",
    [switch]$Fail,
    [switch]$IncludeUntracked
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $patterns = @(
        '^Source/PioneerFramework/Base/',
        '^Source/PioneerFramework/Components/',
        '^Source/PioneerFramework/Combat/',
        '^Source/PioneerFramework/Launch/',
        '^Source/PioneerFramework/GroundAssembly/',
        '^Source/PioneerFramework/Landing/',
        '^Source/PioneerFramework/Celestial/Adapters/Physics/',
        '^1\.7/Assemblies/'
    )

    $changed = @()
    $changed += @(git -c core.quotepath=false diff --name-only $Base 2>&1 |
                  Where-Object { $_ -is [string] -and $_ -notmatch '^(warning|fatal|error):' })
    if ($IncludeUntracked) {
        $changed += @(git -c core.quotepath=false ls-files --others --exclude-standard 2>&1 |
                      Where-Object { $_ -is [string] -and $_ -notmatch '^(warning|fatal|error):' })
    }
    $changed = $changed | Where-Object { $_ } | Sort-Object -Unique

    $backend = @()
    foreach ($f in $changed) {
        foreach ($p in $patterns) {
            if ($f -match $p) { $backend += $f; break }
        }
    }

    Write-Host "=== Backend lock check (base: $Base) ===" -ForegroundColor Cyan
    Write-Host ("changed paths: {0}  |  backend paths: {1}" -f $changed.Count, $backend.Count)

    if ($backend.Count -eq 0) {
        Write-Host "OK: no backend change detected." -ForegroundColor Green
        exit 0
    }

    Write-Host "Backend files touched:" -ForegroundColor Yellow
    $backend | ForEach-Object { Write-Host ("  - " + $_) }

    $authDir = Join-Path $root 'Source/PioneerFramework/DesignNotes'
    $auth = @(Get-ChildItem -LiteralPath $authDir -Filter '*.md' -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -like 'backend-change-auth-*' -or $_.Name -like '后端变更授权-*' })

    if ($auth.Count -gt 0) {
        Write-Host ("Authorization note found: " + ($auth | ForEach-Object { $_.Name }) -join ', ') -ForegroundColor Green
        Write-Host "OK (remember: evidence + criteria + rollback are still required)." -ForegroundColor Green
        exit 0
    }

    Write-Host "VIOLATION: backend changed without an authorization note." -ForegroundColor Red
    Write-Host "  Required: Source/PioneerFramework/DesignNotes/后端变更授权-<date>-<topic>.md"
    Write-Host "  See BACKEND-LOCK.md (rules 1-3). AI must propose, owner commits."
    Write-Host "  AI ASSISTANT: STOP NOW. Print this marker, then ask the user:" -ForegroundColor Red
    Write-Host "    NOSTEPANYMORE" -ForegroundColor Red
    if ($Fail) { exit 1 }
    exit 0
}
finally {
    Pop-Location
}
