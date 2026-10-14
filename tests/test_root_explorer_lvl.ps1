<#
.SYNOPSIS
    LaForge Phase 6 Integration Test:
    1. Provisions/boots the headless AVD (LaForge_x86_64_API34)
    2. Applies AOSP services.jar signature spoofing patch BEFORE microG injection
    3. Injects microG Services Core + Custom Vending LVL Stub (com.android.vending)
    4. Runs CPU & Logcat Diagnostic Check (top -b -n 1 -m 5 + SecurityException / LaForgeSpoof audit)
    5. Installs Root Explorer APK (/tests/test_assets/root_explorer.apk) + LVL shim
    6. Launches via 'adb shell monkey -p com.speedsoftware.rootexplorer -c android.intent.category.LAUNCHER 1'
    7. Asserts ILicensingService interception and 0x0001 (NOT_LICENSED) callback with zero crashes.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path $PSScriptRoot -Parent
$adb        = Join-Path $repoRoot "runtime\platform-tools\adb.exe"
$emu        = Join-Path $repoRoot "runtime\emulator\emulator.exe"
$patchedJar = Join-Path $repoRoot "runtime\gms\services_patched.jar"
$microGApk  = Join-Path $repoRoot "runtime\gms\microg-gms-core.apk"
$vendingApk = Join-Path $repoRoot "runtime\gms\vending-licensing-stub.apk"
$shimApk    = Join-Path $repoRoot "runtime\gms\root-explorer-lvl-client.apk"
$rootExpApk = Join-Path $repoRoot "tests\test_assets\root_explorer.apk"

if (-not (Test-Path $rootExpApk)) {
    $sample = Get-ChildItem (Join-Path $repoRoot "samples") -Filter "*root*explorer*.apk" | Select-Object -First 1
    if ($sample) {
        New-Item -ItemType Directory -Path (Split-Path $rootExpApk -Parent) -Force | Out-Null
        Copy-Item $sample.FullName $rootExpApk -Force
    }
}

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " [LaForge Phase 6] GMS Signature Spoofing & Root Explorer LVL Test" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

# 1. Ensure AVD is running
$devs = & $adb devices
if ($devs -notmatch "emulator-\d+\s+device") {
    Write-Host "[Step 1/6] Booting headless AVD LaForge_x86_64_API34..." -ForegroundColor Yellow
    $env:ANDROID_SDK_ROOT = Join-Path $repoRoot "runtime"
    Start-Process -FilePath $emu -ArgumentList @(
        "-avd", "LaForge_x86_64_API34",
        "-no-window",
        "-gpu", "host",
        "-no-boot-anim",
        "-allow-host-audio",
        "-feature", "WindowsHypervisorPlatform"
    ) -WindowStyle Hidden | Out-Null

    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        $boot = (& $adb shell getprop sys.boot_completed 2>$null)
        if ($boot -and $boot.Trim() -eq "1") { break }
    }
} else {
    Write-Host "[Step 1/6] Headless AVD LaForge_x86_64_API34 is online." -ForegroundColor Green
}

# 2. Apply Build-Time / Provisioning-Time AOSP Signature Spoofing Patch BEFORE microG injection
$spoofActive = (& $adb shell getprop debug.laforge.sigspoof_active 2>$null).Trim()
if ($spoofActive -ne "1") {
    Write-Host "[Step 2/6] Applying AOSP services.jar signature spoofing patch..." -ForegroundColor Yellow
    & $adb root | Out-Null
    & $adb wait-for-device
    & $adb push $patchedJar /data/system/laforge_services.jar | Out-Null

    $mountCmd = @(
        "chmod 644 /data/system/laforge_services.jar",
        "chown root:root /data/system/laforge_services.jar",
        "chcon u:object_r:system_file:s0 /data/system/laforge_services.jar",
        "umount /system/framework/services.jar 2>/dev/null",
        "mount -o bind /data/system/laforge_services.jar /system/framework/services.jar",
        "if [ ! -f /data/system/laforge_services.odex ]; then /apex/com.android.art/bin/dex2oat64 --dex-file=/system/framework/services.jar --oat-file=/data/system/laforge_services.odex --app-image-file=/data/system/laforge_services.art --instruction-set=x86_64 --compiler-filter=verify 2>/dev/null; fi",
        "chmod 644 /data/system/laforge_services.*",
        "chown root:root /data/system/laforge_services.*",
        "chcon u:object_r:system_file:s0 /data/system/laforge_services.*",
        "umount /system/framework/oat/x86_64/services.art 2>/dev/null",
        "umount /system/framework/oat/x86_64/services.odex 2>/dev/null",
        "umount /system/framework/oat/x86_64/services.vdex 2>/dev/null",
        "mount -o bind /data/system/laforge_services.art /system/framework/oat/x86_64/services.art",
        "mount -o bind /data/system/laforge_services.odex /system/framework/oat/x86_64/services.odex",
        "mount -o bind /data/system/laforge_services.vdex /system/framework/oat/x86_64/services.vdex",
        "setprop sys.boot_completed 0; stop; start"
    ) -join "; "

    & $adb shell $mountCmd
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $boot = (& $adb shell getprop sys.boot_completed 2>$null)
        if ($boot -and $boot.Trim() -eq "1") {
            & $adb shell setprop debug.laforge.sigspoof_active 1
            break
        }
    }
} else {
    & $adb root | Out-Null
    & $adb shell "setenforce 0 2>/dev/null"
    Write-Host "[Step 2/6] AOSP services.jar signature spoofing patch already active." -ForegroundColor Green
}

# 3. Inject microG Services Core + Custom Vending LVL Stub
Write-Host "[Step 3/6] Injecting microG Services Core + Custom Vending LVL Stub..." -ForegroundColor Cyan
& $adb install -r -g --force-queryable $microGApk | Out-Null
& $adb install -r -g --force-queryable $vendingApk | Out-Null
& $adb shell "pm grant com.google.android.gms android.permission.FAKE_PACKAGE_SIGNATURE 2>/dev/null; pm grant com.android.vending android.permission.FAKE_PACKAGE_SIGNATURE 2>/dev/null"

# 4. Diagnostic Check (CPU top -b -n 1 -m 5 + Logcat SecurityException / LaForgeSpoof audit)
Write-Host "[Step 4/6] Running Post-Injection Diagnostic Check (top -m 5 & logcat)..." -ForegroundColor Cyan
$topOut = & $adb shell top -b -n 1 -m 5
$topOut | ForEach-Object { Write-Host "  $_" }

# 5. Install Root Explorer APK + LVL Shim and launch via monkey
Write-Host "[Step 5/6] Installing Root Explorer APK (/tests/test_assets/root_explorer.apk)..." -ForegroundColor Cyan
& $adb install -r -g $rootExpApk | Out-Null
if (Test-Path $shimApk) {
    & $adb install -r -g $shimApk | Out-Null
}

& $adb shell logcat -c
Write-Host "[Step 6/6] Launching via: adb shell monkey -p com.speedsoftware.rootexplorer -c android.intent.category.LAUNCHER 1" -ForegroundColor Cyan
$monkeyOut = (& $adb shell monkey -p com.speedsoftware.rootexplorer -c android.intent.category.LAUNCHER 1) -join "`n"
if ($monkeyOut -notmatch "Events injected:\s*1") {
    throw "Monkey failed to inject launcher event: $monkeyOut"
}
Start-Sleep -Seconds 2

$logLines = & $adb shell logcat -d
$spoofLines = @($logLines | Where-Object { $_ -match "I\s+LaForgeSpoof:" })
$interceptLines = @($logLines | Where-Object { $_ -match "LaForgeVendingStub:.*ILicensingService\.checkLicense intercepted" })
$notLicensedLines = @($logLines | Where-Object { $_ -match "(LaForgeVendingStub|RootExplorerLVL):.*0x0001.*NOT_LICENSED" })
$gmsSecEx = @($logLines | Where-Object { $_ -match "SecurityException" -and $_ -match "(com\.google\.android\.gms|FAKE_PACKAGE_SIGNATURE)" })
$fatalEx = @($logLines | Where-Object { $_ -match "FATAL EXCEPTION" })

Write-Host "`n--- Diagnostic & LVL Interception Assertions ---" -ForegroundColor Yellow
Write-Host "  LaForgeSpoof Events         : $($spoofLines.Count)"
$spoofLines | Select-Object -Last 4 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
Write-Host "  GMS Signature Exceptions    : $($gmsSecEx.Count)"
Write-Host "  LVL Intercept Log           : $($interceptLines | Select-Object -Last 1)" -ForegroundColor Green
Write-Host "  LVL Callback Result         : $($notLicensedLines | Select-Object -Last 1)" -ForegroundColor Green

if ($gmsSecEx.Count -gt 0) {
    throw "GMS signature SecurityException detected: $($gmsSecEx -join '; ')"
}
if ($fatalEx.Count -gt 0) {
    throw "FATAL EXCEPTION detected during Root Explorer LVL check: $($fatalEx -join '; ')"
}
if ($interceptLines.Count -eq 0 -or $notLicensedLines.Count -eq 0) {
    throw "Failed to observe ILicensingService interception returning 0x0001 (NOT_LICENSED)."
}

Write-Host "`n[PASS] All 3 Phase 6 GMS/Spoofing/LVL verification checks succeeded!" -ForegroundColor Green
