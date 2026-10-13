<#
.SYNOPSIS
    LaForge Headless Android x86_64 Runtime Provisioner
.DESCRIPTION
    Downloads Google's official Android Command-Line Tools into C:\ag\APKForge\runtime\,
    installs the Android Emulator and API 34 x86_64 system image, and creates the
    headless 'LaForge_x86_64_API34' AVD with WHPX hardware acceleration enabled.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runtimeRoot = Join-Path $PSScriptRoot "runtime"
if (-not (Test-Path -LiteralPath $runtimeRoot)) {
    New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " LaForge Headless Android x86_64 Runtime Provisioner" -ForegroundColor Cyan
Write-Host " Target SDK Root: $runtimeRoot" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# Check for Java (required by sdkmanager / avdmanager)
$javaCmd = Get-Command java -ErrorAction SilentlyContinue
if ($null -eq $javaCmd) {
    Write-Host "[1/4] Installing Microsoft OpenJDK 17 via winget (required by Android sdkmanager)..." -ForegroundColor Yellow
    winget install --id Microsoft.OpenJDK.17 -e --source winget --accept-package-agreements --accept-source-agreements --silent
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
}

$cmdlineToolsDir = Join-Path $runtimeRoot "cmdline-tools\latest"
$sdkManagerBat   = Join-Path $cmdlineToolsDir "bin\sdkmanager.bat"
$avdManagerBat   = Join-Path $cmdlineToolsDir "bin\avdmanager.bat"

if (-not (Test-Path -LiteralPath $sdkManagerBat)) {
    Write-Host "[2/4] Downloading Google Android Command-Line Tools..." -ForegroundColor Yellow
    $zipUrl  = "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip"
    $zipFile = Join-Path $env:TEMP "commandlinetools-win-latest.zip"
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipFile

    $extractTmp = Join-Path $env:TEMP "cmdline_extract"
    if (Test-Path $extractTmp) { Remove-Item $extractTmp -Recurse -Force }
    Expand-Archive -LiteralPath $zipFile -DestinationPath $extractTmp -Force

    New-Item -ItemType Directory -Path $cmdlineToolsDir -Force | Out-Null
    Copy-Item -Path "$extractTmp\cmdline-tools\*" -Destination $cmdlineToolsDir -Recurse -Force
}

$env:ANDROID_HOME = $runtimeRoot
$env:ANDROID_SDK_ROOT = $runtimeRoot

Write-Host "[3/4] Installing Android Emulator, Platform-Tools, and x86_64 System Image (API 34)..." -ForegroundColor Yellow
"y`ny`ny`ny`ny`ny`ny`n" | & $sdkManagerBat --sdk_root=$runtimeRoot "platform-tools" "emulator" "platforms;android-34" "system-images;android-34;default;x86_64"

Write-Host "[4/4] Creating Headless AVD 'LaForge_x86_64_API34'..." -ForegroundColor Yellow
"no`n" | & $avdManagerBat create avd --force --name "LaForge_x86_64_API34" --package "system-images;android-34;default;x86_64" --abi "x86_64"

Write-Host "`n[SUCCESS] Headless Android x86_64 runtime provisioned in $runtimeRoot!" -ForegroundColor Green
Write-Host "You can now click 'Boot Runtime & Attach scrcpy' in any LaForge launcher window." -ForegroundColor Green
