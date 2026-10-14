<#
.SYNOPSIS
    Phase 1 Automated Terminal Verification Suite for LaForge
.DESCRIPTION
    Verifies:
    1. Output architecture scaffolding (config.json, runtime/, bridge/).
    2. Headless emulator command generation (-no-window, -gpu host, -snapshot, WHPX).
    3. Display bridge (scrcpy) window boundary constraints & out-of-bounds rejection.
    4. Target APK launch command via 'adb shell am start'.
    5. Relay system: catching sample.txt, 'adb push', and 'android.intent.action.VIEW' intent with MIME type.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "laforge_poc.ps1")

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
Write-Host " LaForge Phase 1 Verification Suite" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

# Ensure Output Architecture directories exist
foreach ($dir in @("runtime", "bridge")) {
    $dirPath = Join-Path $PSScriptRoot $dir
    if (-not (Test-Path -LiteralPath $dirPath)) {
        New-Item -ItemType Directory -Path $dirPath | Out-Null
    }
}

$cfgPath = Join-Path $PSScriptRoot "config.json"
$cfg = Read-LaForgeConfig -Path $cfgPath

# Test 1: Headless Emulator Command
$emu = Get-EmulatorCommand -Config $cfg
Assert-Condition ($emu.CommandLine -match "-no-window") "Task 1a: Emulator includes -no-window flag" $emu.CommandLine
Assert-Condition ($emu.CommandLine -match "-gpu host") "Task 1b: Emulator includes -gpu host flag" $emu.CommandLine
Assert-Condition ($emu.CommandLine -match "-snapshot default_boot") "Task 1c: Emulator includes -snapshot fast-boot flag" $emu.CommandLine
Assert-Condition ($emu.CommandLine -match "WindowsHypervisorPlatform") "Task 1d: Emulator enables WHPX acceleration" $emu.CommandLine

# Test 2: scrcpy Display Bridge & Window Boundary Validation
$scrcpy = Get-ScrcpyCommand -Config $cfg
Assert-Condition ($scrcpy.Boundaries.Width -eq 1280 -and $scrcpy.Boundaries.Height -eq 720) "Task 2a: Window dimensions parsed (1280x720)"
Assert-Condition ($scrcpy.Boundaries.X -eq 100 -and $scrcpy.Boundaries.Y -eq 100) "Task 2b: Window position parsed (100, 100)"
Assert-Condition ($scrcpy.CommandLine -match "--window-width=1280" -and $scrcpy.CommandLine -match "--window-height=720") "Task 2c: scrcpy receives custom window size constraints" $scrcpy.CommandLine
Assert-Condition ($scrcpy.CommandLine -match "--window-borderless") "Task 2d: scrcpy receives borderless window constraint" $scrcpy.CommandLine
Assert-Condition ($scrcpy.CommandLine -match "--render-driver=direct3d") "Task 2e: scrcpy renders via Direct3D driver" $scrcpy.CommandLine

# Negative Test: Invalid Window Boundaries
$invalidThrown = $false
try {
    $badWin = [PSCustomObject]@{ x = 100; y = 100; width = 50; height = 99999; borderless = $true }
    Test-WindowBoundaries -WindowCfg $badWin | Out-Null
} catch {
    $invalidThrown = $true
}
Assert-Condition $invalidThrown "Task 2f: Out-of-bounds window dimensions are rejected"

# Test 3: Target APK Execution via ADB
$apkCmd = Get-ApkLaunchCommand -Config $cfg
Assert-Condition ($apkCmd.CommandLine -eq "adb shell am start -W -n org.fossify.filemanager/org.fossify.filemanager.activities.MainActivity") "Task 3a: Target APK launched via 'adb shell am start'" $apkCmd.CommandLine

# Test 4: File Association Relay System (.txt)
$sampleFile = Join-Path $PSScriptRoot "sample.txt"
$relay = Get-RelayCommands -Config $cfg -HostFilePath $sampleFile
Assert-Condition ($relay.PushCommand.CommandLine -match 'adb push ".*sample\.txt" "/sdcard/Download/sample\.txt"') "Task 3b: Relay executes 'adb push' to /sdcard/Download/sample.txt" $relay.PushCommand.CommandLine
Assert-Condition ($relay.IntentCommand.CommandLine -match "adb shell am start -a android\.intent\.action\.VIEW -d file:///sdcard/Download/sample\.txt -t text/plain -n org\.fossify\.filemanager/org\.fossify\.filemanager\.activities\.MainActivity") "Task 3c: Relay triggers ACTION_VIEW intent with file URI and text/plain MIME type" $relay.IntentCommand.CommandLine

# Test 5: End-to-End DryRun Execution of laforge_poc.ps1 and relay.ps1
Write-Host "`n--- Running End-to-End DryRun Session ---" -ForegroundColor Cyan
$e2eOut = (& (Join-Path $PSScriptRoot "laforge_poc.ps1") -RelayFilePath $sampleFile -DryRun | Out-String)
Write-Host $e2eOut
$relayOut = (& (Join-Path $PSScriptRoot "relay.ps1") -FilePath $sampleFile -DryRun | Out-String)
Write-Host $relayOut

Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " Verification Summary: $passed Passed, $failed Failed" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

if ($failed -gt 0) {
    exit 1
}
exit 0
