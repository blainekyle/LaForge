<#
.SYNOPSIS
    Phase 2 Automated Terminal Verification Suite for Native launcher.exe
.DESCRIPTION
    Verifies:
    1. Native compilation output (launcher.exe exists in workspace root).
    2. Step 1/4: Headless emulator boot arguments (-no-window, -gpu host, -snapshot default_boot, WHPX).
    3. Step 2/4: Display bridge (scrcpy) window boundary constraints & Direct3D driver.
    4. Step 3/4: Target APK execution via 'adb shell am start'.
    5. Step 4/4: Native Windows file association argument handling (launcher.exe sample.txt -> adb push -> ACTION_VIEW intent).
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
Write-Host " LaForge Phase 2 Native Launcher Verification Suite" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

$launcherExe = Join-Path $PSScriptRoot "launcher.exe"
$sampleFile   = Join-Path $PSScriptRoot "sample.txt"

Assert-Condition (Test-Path -LiteralPath $launcherExe) "Phase 2a: Native launcher.exe binary exists" $launcherExe

$output = (& $launcherExe --dry-run $sampleFile 2>&1 | Out-String)
Write-Host $output

Assert-Condition ($LASTEXITCODE -eq 0) "Phase 2b: launcher.exe exited with code 0"
Assert-Condition ($output -match "emulator -avd LaForge_x86_64_API34 -no-window -gpu host -snapshot default_boot") "Phase 2c: launcher.exe boots headless Android runtime with -snapshot"
Assert-Condition ($output -match "--window-width=1280 --window-height=720 --render-driver=direct3d --max-fps=60 --window-borderless") "Phase 2d: launcher.exe initializes scrcpy display bridge with window boundaries"
Assert-Condition ($output -match "adb shell am start -W -n org\.fossify\.filemanager/org\.fossify\.filemanager\.activities\.MainActivity") "Phase 2e: launcher.exe executes target APK via adb shell am start"
Assert-Condition ($output -match 'adb push ".*sample\.txt" "/sdcard/Download/sample\.txt"') "Phase 2f: launcher.exe relays positional file association via adb push"
Assert-Condition ($output -match "adb shell am start -a android\.intent\.action\.VIEW -d file:///sdcard/Download/sample\.txt -t text/plain") "Phase 2g: launcher.exe dispatches ACTION_VIEW intent with MIME type"

Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " Phase 2 Verification Summary: $passed Passed, $failed Failed" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

if ($failed -gt 0) {
    exit 1
}
exit 0
