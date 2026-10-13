<#
.SYNOPSIS
    LaForge File Association Relay Handler (Phase 1 Task 3)
.DESCRIPTION
    Catches a host file passed via Windows file associations (e.g., .ica),
    pushes it to the Android guest via 'adb push', and fires an ACTION_VIEW
    intent with the configured MIME type.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$FilePath,

    [Parameter(Mandatory = $false)]
    [string]$ConfigPath = (Join-Path $PSScriptRoot "config.json"),

    [Parameter(Mandatory = $false)]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$isDryRun = $DryRun.IsPresent
. (Join-Path $PSScriptRoot "laforge_poc.ps1") -DryRun:$isDryRun

$cfg = Read-LaForgeConfig -Path $ConfigPath
$relayPlan = Get-RelayCommands -Config $cfg -HostFilePath $FilePath

Write-Host "[LaForge Relay] Intercepted Host File: $($relayPlan.HostFile)" -ForegroundColor Green
Write-Host "  Target Guest Path : $($relayPlan.RemotePath)"
Write-Host "  Target Intent URI : $($relayPlan.RemoteUri)"
Write-Host "  Resolved MIME Type: $($relayPlan.MimeType)"
Write-Host "  ADB Push Command  : $($relayPlan.PushCommand.CommandLine)"
Write-Host "  ADB Intent Command: $($relayPlan.IntentCommand.CommandLine)"

if (-not $DryRun) {
    & $relayPlan.PushCommand.Executable @($relayPlan.PushCommand.Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "adb push failed with exit code $LASTEXITCODE"
    }
    & $relayPlan.IntentCommand.Executable @($relayPlan.IntentCommand.Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "adb shell am start failed with exit code $LASTEXITCODE"
    }
}
