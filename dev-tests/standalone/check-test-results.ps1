<#
.SYNOPSIS
    Summarises the results of the Corney standalone test crontab (CORNEY-012).

.DESCRIPTION
    Reads results\*.log written by the test jobs and the newest Corney log in ..\log,
    and prints PASS/FAIL/WAIT per test case. Run it after Corney has been running for
    at least ~15 minutes (the long-running case needs 10 + 6.5 minutes).

.PARAMETER Reset
    Delete results\ (start a fresh measurement) and exit.
#>
param (
    [switch]$Reset
)

$dir = $PSScriptRoot
$results = Join-Path $dir "results"
$logDir = Join-Path (Split-Path $dir -Parent) "log"

if ($Reset) {
    Remove-Item -LiteralPath $results -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Results cleared: $results"
    exit 0
}

$failed = 0

function Report([string]$name, [string]$status, [string]$detail) {
    $color = @{ PASS = "Green"; FAIL = "Red"; WAIT = "Yellow" }[$status]
    Write-Host ("{0,-5} {1,-42} {2}" -f $status, $name, $detail) -ForegroundColor $color
    if ($status -eq "FAIL") { $script:failed++ }
}

function Read-Result([string]$name) {
    $path = Join-Path $results "$name.log"
    if (Test-Path -LiteralPath $path) { return @(Get-Content -LiteralPath $path) }
    return @()
}

# Key "<date> <H:MM>" of a recorder line ("<date> <time> | cwd=... | args=...").
function Get-MinuteKey([string]$line) {
    if ($line -match '^(.*?\d{1,2}:\d{2}):\d{2}') { return $matches[1].Trim() }
    return $null
}

Write-Host "Test folder: $dir"
Write-Host ""

# 1. ensure-test-assets.ps1 via powershell.exe -File
$ensure = Read-Result "ensure"
if ($ensure.Count -gt 0) { Report "ensure-test-assets (powershell -File)" "PASS" "$($ensure.Count) runs, last: $($ensure[-1])" }
else { Report "ensure-test-assets (powershell -File)" "WAIT" "no runs yet" }

# 2. Duplicate detector: one tick per minute
$tick = Read-Result "tick"
$dups = @($tick | ForEach-Object { Get-MinuteKey $_ } | Where-Object { $_ } | Group-Object | Where-Object { $_.Count -gt 1 })
if ($tick.Count -eq 0) { Report "one run per minute (CORNEY-007)" "WAIT" "no runs yet" }
elseif ($dups.Count -gt 0) {
    $sample = ($dups | Select-Object -First 3 | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join "; "
    Report "one run per minute (CORNEY-007)" "FAIL" "$($dups.Count) minute(s) with duplicates: $sample"
}
else { Report "one run per minute (CORNEY-007)" "PASS" "$($tick.Count) runs, no duplicates" }

# 3. .lnk with arguments
$expectedLnk = 'args=lnk update -p simple-scraper,robe-nova "a b"'
$lnk = Read-Result "lnk"
if ($lnk.Count -eq 0) { Report ".lnk with arguments (CORNEY-008)" "WAIT" "no runs yet" }
elseif ($lnk[-1].EndsWith($expectedLnk)) { Report ".lnk with arguments (CORNEY-008)" "PASS" "$($lnk.Count) runs" }
else { Report ".lnk with arguments (CORNEY-008)" "FAIL" "last: $($lnk[-1])" }

# 4. .cmd in a path with a space, quoted argument
$expectedCmd = 'args=cmd "quoted arg" plain'
$cmd = Read-Result "cmd"
if ($cmd.Count -eq 0) { Report ".cmd, path with space, quoted arg" "WAIT" "no runs yet" }
elseif ($cmd[-1].EndsWith($expectedCmd) -and $cmd[-1].Contains("cwd=$dir")) { Report ".cmd, path with space, quoted arg" "PASS" "$($cmd.Count) runs" }
else { Report ".cmd, path with space, quoted arg" "FAIL" "last: $($cmd[-1])" }

# 5. Long-running process is not killed
$long = Read-Result "long"
$starts = @($long | Where-Object { $_ -like "start *" }).Count
$ends = @($long | Where-Object { $_ -like "end *" }).Count
if ($starts -eq 0) { Report "process > 5 min not killed (CORNEY-008)" "WAIT" "not started yet (every 10 min)" }
elseif ($ends -eq 0) { Report "process > 5 min not killed (CORNEY-008)" "WAIT" "$starts started, none finished yet (~6.5 min)" }
else { Report "process > 5 min not killed (CORNEY-008)" "PASS" "$starts started, $ends finished" }

# 6. Corney log: missing program reported, nothing unexpected
$log = Get-ChildItem -LiteralPath $logDir -Filter "corney.*.txt" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $log) {
    Report "Corney log" "WAIT" "no log in $logDir"
}
else {
    $lines = @(Get-Content -LiteralPath $log.FullName | Where-Object { $_ -match '^\S+ \S+ (WARN|ERROR|FATAL) ' })
    $missing = @($lines | Where-Object { $_ -like "*Failed to execute cron job*missing-program.exe*" })
    $late = @($lines | Where-Object { $_ -like "*Cron tick*late by*" })
    # Expected: the deliberate missing-program job has a 0% success rate in the hourly report
    # (process.execution.missing-program). Every real job failure has its own ERROR line.
    $batchRate = @($lines | Where-Object { $_ -like "*High failure rate detected: process.execution.missing-program*" })
    $other = @($lines | Where-Object {
        $_ -notlike "*missing-program.exe*" -and $_ -notlike "*Cron tick*late by*" -and
        $_ -notlike "*High failure rate detected: process.execution.missing-program*" })

    if ($missing.Count -gt 0) { Report "missing program -> ERROR, Corney runs on" "PASS" "$($missing.Count) error(s) in $($log.Name)" }
    else { Report "missing program -> ERROR, Corney runs on" "WAIT" "no error yet (every 5 min)" }

    Write-Host ("INFO  {0,-42} {1}" -f "late ticks after sleep (CORNEY-011)", "$($late.Count) in $($log.Name)")
    Write-Host ("INFO  {0,-42} {1}" -f "missing-program failure-rate warnings", "$($batchRate.Count) (expected)")

    if ($other.Count -eq 0) { Report "no other WARN/ERROR in Corney log" "PASS" $log.Name }
    else {
        Report "no other WARN/ERROR in Corney log" "FAIL" "$($other.Count) line(s), first:"
        $other | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" }
    }
}

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed test case(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "No failures (WAIT = not enough time yet)." -ForegroundColor Green
exit 0
