<#
.SYNOPSIS
    Tests on Windows that Corney starts a .lnk shortcut with arguments (CORNEY-008).

.DESCRIPTION
    1. Creates a test folder with target.cmd, which appends the arguments and the
       working directory it receives to result.txt.
    2. Creates test.lnk pointing to target.cmd (via WScript.Shell).
    3. Runs the Windows-only test ProcessWrapperWindowsTests.Start_Shortcut_RunsTargetWithArguments
       with CORNEY_TEST_LNK set. The test starts the shortcut through the real ProcessWrapper,
       the same way Corney starts SyrupEvolve.lnk, and checks result.txt.
    4. Prints result.txt and a crontab line for a manual test in a running Corney.

    Without CORNEY_TEST_LNK the test creates its own shortcut (that is how it runs on CI);
    on non-Windows systems it is skipped.

.PARAMETER Keep
    Keep the test folder (needed for the manual crontab test).

.EXAMPLE
    .\scripts\test-lnk.ps1
    .\scripts\test-lnk.ps1 -Keep
#>
param (
    [switch]$Keep
)

$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
    throw "This script needs Windows (.lnk shortcuts and WScript.Shell)."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "src\Corney.Tests\Corney.Tests.csproj"
if (-not (Test-Path $testProject)) {
    throw "Test project not found: $testProject"
}

# A space in the path checks quoting of the program path as well.
$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ("corney lnk test " + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $testDir | Out-Null

$target = Join-Path $testDir "target.cmd"
$shortcut = Join-Path $testDir "test.lnk"
$result = Join-Path $testDir "result.txt"

# --------- target.cmd: records what it receives ---------
$cmd = @(
    '@echo off',
    '>> "%~dp0result.txt" echo time=%date% %time%',
    '>> "%~dp0result.txt" echo cwd=%cd%',
    '>> "%~dp0result.txt" echo args=%*'
)
Set-Content -Path $target -Value $cmd -Encoding Ascii

# --------- test.lnk -> target.cmd ---------
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $target
$lnk.WorkingDirectory = $testDir
$lnk.Save()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

Write-Host "Test folder: $testDir"
Write-Host "Shortcut:    $shortcut -> $target"
Write-Host ""

# --------- run the test ---------
$env:CORNEY_TEST_LNK = $shortcut
try {
    & dotnet test $testProject --filter "FullyQualifiedName~ProcessWrapperWindowsTests.Start_Shortcut_RunsTargetWithArguments" --logger "console;verbosity=normal"
    $testExitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:\CORNEY_TEST_LNK -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "--------- result.txt ---------"
if (Test-Path $result) {
    Get-Content $result | Write-Host
}
else {
    Write-Host "(missing: the shortcut target did not run)"
}
Write-Host "------------------------------"
Write-Host ""

# A skipped test also exits with 0, so the shortcut target must have written result.txt.
$targetRan = (Test-Path $result) -and ((Get-Content $result -Raw) -match "args=")
if ($testExitCode -eq 0 -and $targetRan) {
    Write-Host "PASS: Corney started the .lnk shortcut with its arguments." -ForegroundColor Green
}
else {
    if ($testExitCode -eq 0) { $testExitCode = 1 }
    Write-Host "FAIL: dotnet test exit code $testExitCode, target ran: $targetRan (see output above)." -ForegroundColor Red
}

if ($Keep) {
    Write-Host ""
    Write-Host "Manual test: add this line to a crontab file watched by a running Corney,"
    Write-Host "wait for the next minute and check result.txt and the Corney log:"
    Write-Host ""
    Write-Host ("* * * * * `"{0}`" update -p simple-scraper,robe-nova `"a b`"" -f $shortcut)
    Write-Host ""
    Write-Host "Remove the line and the folder when done: $testDir"
}
else {
    Remove-Item -Recurse -Force $testDir
}

exit $testExitCode
