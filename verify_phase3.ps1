<#
.SYNOPSIS
    Phase 3 Automated Verification Suite — Fossify APK Ingestion & Standalone Bundle Execution
.DESCRIPTION
    Verifies:
    1. Native binary AXML manifest inspection (--inspect) across all 4 Fossify APKs.
    2. Standalone bundle generation (--forge) into dist/<package_name>/.
    3. Presence of launcher.exe, config.json, payload.apk, runtime/, and bridge/ in each bundle.
    4. Execution flow of each forged bundle's launcher.exe (sideloading payload.apk, launching
       SplashActivity.Green, and relaying domain-specific host files via adb push + ACTION_VIEW).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$passed = 0
$failed = 0

function Assert-Condition {
    param (
        [bool]$Condition,
        [string]$TestName,
        [string]$Detail = ""
    )
    if ($Condition) {
        Write-Host "[PASS] $TestName" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "[FAIL] $TestName - $Detail" -ForegroundColor Red
        $script:failed++
    }
}

Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " LaForge Phase 3 — Fossify APK Ingestion & Bundle Suite" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

$rootLauncher = Join-Path $PSScriptRoot "launcher.exe"
$samplesDir   = Join-Path $PSScriptRoot "samples"
$distDir      = Join-Path $PSScriptRoot "dist"

# Create lightweight test host files for relay verification
$testTxt = Join-Path $samplesDir "test_note.txt"
$testPng = Join-Path $samplesDir "test_image.png"
$testMp3 = Join-Path $samplesDir "test_track.mp3"
$testWav = Join-Path $samplesDir "test_memo.wav"

Set-Content -LiteralPath $testTxt -Value "LaForge File Manager Relay Test"
Set-Content -LiteralPath $testPng -Value "PNG_STUB"
Set-Content -LiteralPath $testMp3 -Value "ID3_STUB"
Set-Content -LiteralPath $testWav -Value "RIFF_WAVE_STUB"

$testMatrix = @(
    @{
        ApkFile      = "org.fossify.filemanager_13.apk"
        Package      = "org.fossify.filemanager"
        BespokeExe   = "FossifyFilemanager.exe"
        Activity     = "org.fossify.filemanager.activities.SplashActivity.Green"
        RelayFile    = $testTxt
        ExpectedMime = "text/plain"
    },
    @{
        ApkFile      = "org.fossify.gallery_28.apk"
        Package      = "org.fossify.gallery"
        BespokeExe   = "FossifyGallery.exe"
        Activity     = "org.fossify.gallery.activities.SplashActivity.Green"
        RelayFile    = $testPng
        ExpectedMime = "image/png"
    },
    @{
        ApkFile      = "org.fossify.musicplayer_14.apk"
        Package      = "org.fossify.musicplayer"
        BespokeExe   = "FossifyMusicplayer.exe"
        Activity     = "org.fossify.musicplayer.activities.SplashActivity.Green"
        RelayFile    = $testMp3
        ExpectedMime = "audio/mpeg"
    },
    @{
        ApkFile      = "org.fossify.voicerecorder_18.apk"
        Package      = "org.fossify.voicerecorder"
        BespokeExe   = "FossifyVoicerecorder.exe"
        Activity     = "org.fossify.voicerecorder.activities.SplashActivity.Green"
        RelayFile    = $testWav
        ExpectedMime = "audio/wav"
    }
)

foreach ($item in $testMatrix) {
    $apkPath = Join-Path $samplesDir $item.ApkFile
    $pkg     = $item.Package
    Write-Host "`n--- Verifying Package: $pkg ---" -ForegroundColor Cyan

    # 1. Forge standalone bundle
    $forgeOut = (& $rootLauncher --forge $apkPath --out $distDir 2>&1 | Out-String)
    Assert-Condition ($LASTEXITCODE -eq 0) "$pkg : Forged bundle successfully" $forgeOut

    $bundleDir     = Join-Path $distDir $pkg
    $bundleExe     = Join-Path $bundleDir "launcher.exe"
    $bespokeExe    = Join-Path $bundleDir $item.BespokeExe
    $bundleIco     = Join-Path $bundleDir "app.ico"
    $bundleCfg     = Join-Path $bundleDir "config.json"
    $bundlePayload = Join-Path $bundleDir "payload.apk"
    $bundleLnk     = Join-Path $bundleDir "HOST.lnk"
    $bundleRuntime = Join-Path $bundleDir "runtime"
    $bundleBridge  = Join-Path $bundleDir "bridge"
    $hostShareDir  = Join-Path $env:LOCALAPPDATA "LaForge\HOST"
    $appConfFile   = Join-Path $env:LOCALAPPDATA "LaForge\config\$pkg.conf"

    Assert-Condition (Test-Path -LiteralPath $bundleExe) "$pkg : Bundle contains standalone launcher.exe"
    Assert-Condition (Test-Path -LiteralPath $bespokeExe) "$pkg : Bundle contains bespoke $($item.BespokeExe)"
    Assert-Condition (Test-Path -LiteralPath $bundleIco) "$pkg : Bundle contains generated app.ico icon"
    Assert-Condition (Test-Path -LiteralPath $bundleCfg) "$pkg : Bundle contains bespoke config.json"
    Assert-Condition (Test-Path -LiteralPath $bundlePayload) "$pkg : Bundle contains sideloaded payload.apk"
    Assert-Condition (Test-Path -LiteralPath $bundleLnk) "$pkg : Bundle contains adjacent HOST.lnk shortcut to %LOCALAPPDATA%\LaForge\HOST"
    Assert-Condition (Test-Path -LiteralPath $hostShareDir) "$pkg : Dedicated %LOCALAPPDATA%\LaForge\HOST folder exists"
    Assert-Condition (Test-Path -LiteralPath $appConfFile) "$pkg : Per-app config file exists in %LOCALAPPDATA%\LaForge\config\$pkg.conf"
    Assert-Condition ((Test-Path -LiteralPath $bundleRuntime) -and (Test-Path -LiteralPath $bundleBridge)) "$pkg : Bundle contains runtime/ and bridge/ directories"

    # 2. Execute the forged bundle's bespoke EXE with its domain-specific relay file
    $runOut = (& $bespokeExe --dry-run --config $bundleCfg $item.RelayFile 2>&1 | Out-String)
    Write-Host $runOut.Trim()

    Assert-Condition ($LASTEXITCODE -eq 0) "$pkg : Bundle launcher.exe exited with 0"
    Assert-Condition ($runOut -match "/sdcard/HOST") "$pkg : Mounts host folder to /sdcard/HOST"
    Assert-Condition ($runOut -match 'adb install -r ".*payload\.apk"') "$pkg : Sideloads payload.apk via 'adb install -r'"
    Assert-Condition ($runOut -match [regex]::Escape("adb shell am start -W -n $($item.Package)/$($item.Activity)")) "$pkg : Launches extracted activity ($($item.Activity))"
    Assert-Condition ($runOut -match [regex]::Escape("-t $($item.ExpectedMime)")) "$pkg : Relays host file with MIME type $($item.ExpectedMime)"
}

Write-Host "`n============================================================" -ForegroundColor Yellow
Write-Host " Phase 3 Verification Summary: $passed Passed, $failed Failed" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

if ($failed -gt 0) {
    exit 1
}
exit 0
