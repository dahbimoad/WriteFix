<#
.SYNOPSIS
    Publishes WriteFix and compiles the Inno Setup installer into dist\.

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -SkipPublish     # reuse the existing publish\ folder
#>
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent

# Inno Setup 6 may be installed per-user rather than into Program Files.
$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install it with: winget install --id JRSoftware.InnoSetup"
}

if (-not $SkipPublish) {
    & "$PSScriptRoot\publish.ps1" | Out-Null
}

$publish = Join-Path $repo "publish"
if (-not (Test-Path (Join-Path $publish "WriteFix.exe"))) {
    throw "publish\WriteFix.exe is missing. Run .\scripts\publish.ps1 first."
}

$distDir = Join-Path $repo "dist"
New-Item -ItemType Directory -Force $distDir | Out-Null

# Inno overwrites the previous setup .exe in place. If antivirus is still holding
# that file from the last build, the write fails and ISCC exits 2 with no message.
# Clearing it first makes the build repeatable.
Get-ChildItem $distDir -Filter "WriteFix-Setup-*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
    for ($i = 1; $i -le 5 -and (Test-Path $_.FullName); $i++) {
        try { Remove-Item $_.FullName -Force -ErrorAction Stop }
        catch {
            Write-Host "  previous installer is locked, retrying ($i/5)..." -ForegroundColor DarkYellow
            Start-Sleep -Seconds 2
        }
    }
}

Write-Host "Compiling installer with $iscc ..." -ForegroundColor Cyan

$attempt = 0
do {
    $attempt++
    # Captured rather than streamed, so a failure shows the real compiler message
    # instead of a bare exit code.
    $isccOutput = & $iscc "$repo\installer\WriteFix.iss" 2>&1
    $code = $LASTEXITCODE

    if ($code -eq 0) { break }

    Write-Host "ISCC attempt $attempt failed (exit $code). Output:" -ForegroundColor Yellow
    $isccOutput |
        Where-Object { $_ -notmatch '^\s*(Compressing|Parsing|Reading):?' } |
        Select-Object -Last 25 |
        ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

    if ($attempt -lt 3) { Start-Sleep -Seconds 3 }
} while ($attempt -lt 3)

if ($code -ne 0) {
    throw "ISCC failed with exit code $code after $attempt attempts (see output above)."
}

$setup = Get-ChildItem $distDir -Filter "WriteFix-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ""
Write-Host "Installer ready." -ForegroundColor Green
Write-Host "  $($setup.FullName)"
Write-Host "  $([math]::Round($setup.Length / 1MB, 1)) MB"
