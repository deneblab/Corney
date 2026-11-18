<#
.SYNOPSIS
    Publishes the Syrup package built by `build.cmd --target Syrup` to Azure Blob Storage.

.DESCRIPTION
    Uploads *.nupkg and *.nupkg.syrup from the build output, keeps the newest releases,
    removes older ones and rewrites syrup.index.json.

    Authentication is left to Azure CLI, nothing secret is printed:
      - connection string: set AZURE_STORAGE_CONNECTION_STRING, or
      - login:             az login, then pass -AccountName.

.EXAMPLE
    .\build.cmd --target Syrup
    .\scripts\publish-syrup.ps1 -DryRun
    .\scripts\publish-syrup.ps1
#>
param (
    [string]$SourceDir,
    [string]$Container = "application-corney",
    [string]$AccountName,
    [int]$Keep = 15,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot ".fallout\temp\w\Corney\syrup" }
$indexName = "syrup.index.json"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) not found. Install it from https://aka.ms/installazurecli"
}
if (-not $AccountName -and -not $env:AZURE_STORAGE_CONNECTION_STRING) {
    throw "Set AZURE_STORAGE_CONNECTION_STRING or pass -AccountName (after az login)."
}

# Common arguments for every `az storage blob` call
$auth = @("--container-name", $Container, "--only-show-errors")
if ($AccountName) { $auth += @("--account-name", $AccountName, "--auth-mode", "login") }

function Invoke-Az {
    param ([string[]]$AzArgs)
    $out = & az @AzArgs
    if ($LASTEXITCODE -ne 0) { throw "az $($AzArgs[0..2] -join ' ') failed with exit code $LASTEXITCODE" }
    return $out
}

$files = @(Get-ChildItem -Path $SourceDir -File -ErrorAction Stop |
    Where-Object { $_.Name -like "*.nupkg" -or $_.Name -like "*.nupkg.syrup" })
if ($files.Count -eq 0) { throw "No *.nupkg / *.nupkg.syrup in $SourceDir. Run: .\build.cmd --target Syrup" }

Write-Host "Container: $Container"
Write-Host "Source:    $SourceDir"
foreach ($f in $files) { Write-Host "  upload $($f.Name)" }
if ($DryRun) { Write-Host "Dry run, nothing sent."; return }

# 1. Upload
foreach ($f in $files) {
    Invoke-Az (@("storage", "blob", "upload") + $auth + @("--file", $f.FullName, "--name", $f.Name, "--overwrite", "true")) | Out-Null
}

# 2. Read every .syrup descriptor from the container
$names = Invoke-Az (@("storage", "blob", "list") + $auth + @("--query", "[?ends_with(name, '.syrup')].name", "--output", "json")) | Out-String | ConvertFrom-Json
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("syrup-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $infos = @()
    foreach ($n in @($names)) {
        $local = Join-Path $tmp $n
        Invoke-Az (@("storage", "blob", "download") + $auth + @("--name", $n, "--file", $local, "--overwrite", "true")) | Out-Null
        $infos += (Get-Content -Raw -Path $local | ConvertFrom-Json)
    }

    # 3. Keep the newest releases, delete the rest
    $sorted = @($infos | Sort-Object -Property ReleaseDate -Descending)
    $remove = @($sorted | Select-Object -Skip $Keep)
    $keepList = @($sorted | Select-Object -First $Keep)
    foreach ($r in $remove) {
        Write-Host "  remove $($r.File)"
        foreach ($blob in @($r.File, "$($r.File).syrup")) {
            Invoke-Az (@("storage", "blob", "delete") + $auth + @("--name", $blob)) | Out-Null
        }
    }

    # 4. Rewrite the index. Container URL is taken from the URL of the index blob itself.
    $indexUrl = (Invoke-Az (@("storage", "blob", "url") + $auth + @("--name", $indexName, "--output", "tsv")) | Out-String).Trim()
    $containerUrl = $indexUrl.Substring(0, $indexUrl.LastIndexOf("/"))
    $index = @($keepList | ForEach-Object {
        [ordered]@{
            App = $_.App; Name = $_.Name; File = $_.File; FileUrl = "$containerUrl/$($_.File)"
            Sha = $_.Sha; SemVer = $_.SemVer; Channel = $_.Channel; ReleaseDate = $_.ReleaseDate
        }
    })
    $indexFile = Join-Path $tmp $indexName
    $json = ConvertTo-Json -InputObject $index -Depth 4
    [IO.File]::WriteAllText($indexFile, $json, (New-Object Text.UTF8Encoding $false))
    Invoke-Az (@("storage", "blob", "upload") + $auth + @("--file", $indexFile, "--name", $indexName, "--overwrite", "true")) | Out-Null

    Write-Host "Published. Releases in container: $($index.Count) (removed $($remove.Count))."
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
