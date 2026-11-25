<#
.SYNOPSIS
    Creates the files used by the Corney standalone test crontab when they are missing.

.DESCRIPTION
    Run every minute by Corney itself (crontab.tests.txt), so deleting any of the files
    is repaired within a minute. Creates, next to this script:
      recorder.cmd  <name> [args...]  appends "<date> <time> | cwd=<dir> | args=<args>" to results\<name>.log
      long.cmd                        writes "start", runs ~6.5 min (ping), writes "end" to results\long.log
      target.lnk                      shortcut to recorder.cmd (working directory: this folder)

    Copied to dev\app.standalone\test env\ by build.cmd PublishLocalStandalone (CORNEY-012).
#>
$ErrorActionPreference = "Stop"

$dir = $PSScriptRoot
$results = Join-Path $dir "results"
New-Item -ItemType Directory -Force -Path $results | Out-Null

function Write-IfMissing([string]$path, [string[]]$lines) {
    if (-not (Test-Path -LiteralPath $path)) {
        Set-Content -LiteralPath $path -Value $lines -Encoding Ascii
    }
}

Write-IfMissing (Join-Path $dir "recorder.cmd") @(
    '@echo off',
    '>> "%~dp0results\%~1.log" echo %date% %time% ^| cwd=%cd% ^| args=%*'
)

Write-IfMissing (Join-Path $dir "long.cmd") @(
    '@echo off',
    '>> "%~dp0results\long.log" echo start %date% %time%',
    'ping -n 400 127.0.0.1 > nul',
    '>> "%~dp0results\long.log" echo end %date% %time%'
)

$shortcut = Join-Path $dir "target.lnk"
if (-not (Test-Path -LiteralPath $shortcut)) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $lnk = $shell.CreateShortcut($shortcut)
        $lnk.TargetPath = Join-Path $dir "recorder.cmd"
        $lnk.WorkingDirectory = $dir
        $lnk.Save()
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

# Proof that this job ran (it is also a test case: powershell.exe -File with a path containing a space)
Add-Content -LiteralPath (Join-Path $results "ensure.log") -Value ("{0:yyyy-MM-dd HH:mm:ss} ok" -f (Get-Date))
