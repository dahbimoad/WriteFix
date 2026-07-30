<#
.SYNOPSIS
    Builds the self-contained win-x64 release of WriteFix into publish\.

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Output "D:\Tools\WriteFix"
#>
param(
    [string]$Output
)

$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $repo "publish" }

$project = Join-Path $repo "src\WriteFix.csproj"

Write-Host "Publishing WriteFix (self-contained, win-x64)..." -ForegroundColor Cyan

# Self-contained so the target machine needs no .NET runtime installed.
# Not single-file: WPF starts faster from a folder and the tray icon resource
# loads cleanly.
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    --output $Output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $Output "WriteFix.exe"
if (-not (Test-Path $exe)) {
    throw "Publish finished but $exe is missing."
}

$size = [math]::Round(((Get-ChildItem $Output -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Executable : $exe"
Write-Host "  Folder size: $size MB"
