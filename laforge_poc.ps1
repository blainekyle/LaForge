<#
.SYNOPSIS
    LaForge Phase 1 Proof-of-Concept Orchestrator (Headless Emulator + scrcpy Display Bridge + File Relay)
.DESCRIPTION
    1. Boots an Android SDK emulator headless (-no-window -gpu host -snapshot).
    2. Waits for ADB boot completion and validates custom window boundary constraints.
    3. Attaches scrcpy with SDL2/Direct3D and custom window constraints.
    4. Executes the target APK via 'adb shell am start'.
    5. Relays host files (e.g., .ica) via 'adb push' and triggers ACTION_VIEW intents with MIME types.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]$ConfigPath = (Join-Path $PSScriptRoot "config.json"),

    [Parameter(Mandatory = $false)]
    [string]$RelayFilePath = "",

    [Parameter(Mandatory = $false)]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-LaForgeConfig {
    param ([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Configuration file not found at: $Path"
    }
    $cfg = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    return $cfg
}

function Test-WindowBoundaries {
    param ($WindowCfg)
    $width  = [int]$WindowCfg.width
    $height = [int]$WindowCfg.height
    $x      = [int]$WindowCfg.x
    $y      = [int]$WindowCfg.y

    if ($width -lt 200 -or $width -gt 7680) {
        throw "Invalid window width ($width). Must be between 200 and 7680 pixels."
    }
    if ($height -lt 200 -or $height -gt 4320) {
        throw "Invalid window height ($height). Must be between 200 and 4320 pixels."
    }
    if ($x -lt -3840 -or $x -gt 7680 -or $y -lt -2160 -or $y -gt 4320) {
        throw "Window coordinates ($x, $y) are out of valid desktop bounds."
    }

    return [PSCustomObject]@{
        Valid      = $true
        X          = $x
        Y          = $y
        Width      = $width
        Height     = $height
        Borderless = [bool]$WindowCfg.borderless
    }
}

function Get-EmulatorCommand {
    param ($Config)
    $rt = $Config.runtime
    $argsList = @(
        "-avd", $rt.avd_name
    )
    if ($rt.headless) {
        $argsList += "-no-window"
    }
    $argsList += @("-gpu", $rt.gpu_mode)
    if ($rt.snapshot) {
        $argsList += @("-snapshot", $rt.snapshot)
    }
    $argsList += @("-no-boot-anim", "-no-audio")
    if ($rt.whpx) {
        $argsList += @("-feature", "WindowsHypervisorPlatform")
    }

    return [PSCustomObject]@{
        Executable = $rt.emulator_path
        Arguments  = $argsList
        CommandLine = "$($rt.emulator_path) $($argsList -join ' ')"
    }
}

function Get-ScrcpyCommand {
    param ($Config)
    $disp = $Config.display
    $win  = Test-WindowBoundaries -WindowCfg $disp.window

    $argsList = @(
        "--window-title=`"$($Config.app.title)`"",
        "--window-x=$($win.X)",
        "--window-y=$($win.Y)",
        "--window-width=$($win.Width)",
        "--window-height=$($win.Height)",
        "--render-driver=$($disp.render_driver)",
        "--max-fps=$($disp.max_fps)"
    )

    if ($win.Borderless) {
        $argsList += "--window-borderless"
    }
    if ($disp.window.always_on_top) {
        $argsList += "--always-on-top"
    }
    if ($disp.window.fullscreen) {
        $argsList += "--fullscreen"
    }
    if ($disp.stay_awake) {
        $argsList += "--stay-awake"
    }

    return [PSCustomObject]@{
        Executable  = $disp.scrcpy_path
        Arguments   = $argsList
        Boundaries  = $win
        CommandLine = "$($disp.scrcpy_path) $($argsList -join ' ')"
    }
}

function Get-ApkLaunchCommand {
    param ($Config)
    $adb = $Config.runtime.adb_path
    $component = "$($Config.app.package_name)/$($Config.app.activity_name)"
    $argsList = @("shell", "am", "start", "-W", "-n", $component)

    return [PSCustomObject]@{
        Executable  = $adb
        Arguments   = $argsList
        CommandLine = "$adb $($argsList -join ' ')"
    }
}

function Get-RelayCommands {
    param (
        $Config,
        [Parameter(Mandatory = $true)]
        [string]$HostFilePath
    )

    if (-not (Test-Path -LiteralPath $HostFilePath)) {
        throw "Relay host file does not exist: $HostFilePath"
    }

    $fileItem = Get-Item -LiteralPath $HostFilePath
    $ext      = $fileItem.Extension.ToLowerInvariant()
    $fileName = $fileItem.Name

    $associations = $Config.relays.associations
    $assocProp = $associations.PSObject.Properties[$ext]
    if ($null -eq $assocProp) {
        throw "Unsupported file extension '$ext' for relay. Configured extensions: $($associations.PSObject.Properties.Name -join ', ')"
    }

    $rule      = $assocProp.Value
    $mimeType  = $rule.mime_type
    $action    = $rule.action
    $remoteDir = $Config.relays.remote_staging_dir.TrimEnd('/')
    $remotePath = "$remoteDir/$fileName"
    $remoteUri  = "file://$remotePath"
    $component  = "$($Config.app.package_name)/$($Config.app.activity_name)"
    $adb        = $Config.runtime.adb_path

    $pushArgs = @("push", $fileItem.FullName, $remotePath)
    $intentArgs = @(
        "shell", "am", "start",
        "-a", $action,
        "-d", $remoteUri,
        "-t", $mimeType,
        "-n", $component
    )

    return [PSCustomObject]@{
        HostFile        = $fileItem.FullName
        RemotePath      = $remotePath
        RemoteUri       = $remoteUri
        MimeType        = $mimeType
        Action          = $action
        PushCommand     = [PSCustomObject]@{
            Executable  = $adb
            Arguments   = $pushArgs
            CommandLine = "$adb push `"$($fileItem.FullName)`" `"$remotePath`""
        }
        IntentCommand   = [PSCustomObject]@{
            Executable  = $adb
            Arguments   = $intentArgs
            CommandLine = "$adb $($intentArgs -join ' ')"
        }
    }
}

function Start-LaForgeSession {
    param (
        $Config,
        [string]$RelayFile,
        [switch]$IsDryRun
    )

    $emuCmd    = Get-EmulatorCommand -Config $Config
    $scrcpyCmd = Get-ScrcpyCommand -Config $Config
    $launchCmd = Get-ApkLaunchCommand -Config $Config

    Write-Host "[LaForge] Step 1/4: Booting Headless Android Runtime..." -ForegroundColor Cyan
    Write-Host "  Command: $($emuCmd.CommandLine)"
    if (-not $IsDryRun) {
        $emuProc = Start-Process -FilePath $emuCmd.Executable -ArgumentList $emuCmd.Arguments -WindowStyle Hidden -PassThru
        Write-Host "  Started Emulator PID: $($emuProc.Id)"

        & $Config.runtime.adb_path wait-for-device
        $deadline = (Get-Date).AddSeconds($Config.runtime.boot_timeout_sec)
        do {
            $bootProp = (& $Config.runtime.adb_path shell getprop sys.boot_completed 2>$null | Out-String).Trim()
            if ($bootProp -eq "1") { break }
            Start-Sleep -Seconds 1
        } while ((Get-Date) -lt $deadline)

        if ($bootProp -ne "1") {
            throw "Android runtime failed to finish booting within $($Config.runtime.boot_timeout_sec)s."
        }
    }

    Write-Host "[LaForge] Step 2/4: Initializing Display Bridge (scrcpy + SDL2/Direct3D)..." -ForegroundColor Cyan
    Write-Host "  Window Boundaries: X=$($scrcpyCmd.Boundaries.X), Y=$($scrcpyCmd.Boundaries.Y), W=$($scrcpyCmd.Boundaries.Width), H=$($scrcpyCmd.Boundaries.Height), Borderless=$($scrcpyCmd.Boundaries.Borderless)"
    Write-Host "  Command: $($scrcpyCmd.CommandLine)"
    if (-not $IsDryRun) {
        $scrcpyProc = Start-Process -FilePath $scrcpyCmd.Executable -ArgumentList $scrcpyCmd.Arguments -PassThru
        Write-Host "  Started scrcpy PID: $($scrcpyProc.Id)"
    }

    Write-Host "[LaForge] Step 3/4: Launching Target APK..." -ForegroundColor Cyan
    Write-Host "  Command: $($launchCmd.CommandLine)"
    if (-not $IsDryRun) {
        & $launchCmd.Executable @($launchCmd.Arguments)
    }

    if (-not [string]::IsNullOrWhiteSpace($RelayFile)) {
        Write-Host "[LaForge] Step 4/4: Executing File Association Relay..." -ForegroundColor Cyan
        $relayPlan = Get-RelayCommands -Config $Config -HostFilePath $RelayFile
        Write-Host "  Host File  : $($relayPlan.HostFile)"
        Write-Host "  Remote URI : $($relayPlan.RemoteUri)"
        Write-Host "  MIME Type  : $($relayPlan.MimeType)"
        Write-Host "  Push Cmd   : $($relayPlan.PushCommand.CommandLine)"
        Write-Host "  Intent Cmd : $($relayPlan.IntentCommand.CommandLine)"

        if (-not $IsDryRun) {
            & $relayPlan.PushCommand.Executable @($relayPlan.PushCommand.Arguments)
            & $relayPlan.IntentCommand.Executable @($relayPlan.IntentCommand.Arguments)
        }
    } else {
        Write-Host "[LaForge] Step 4/4: No initial relay file specified (standing by)." -ForegroundColor DarkGray
    }
}

# Only auto-execute when invoked directly (not dot-sourced by tests or relay.ps1)
if ($MyInvocation.InvocationName -ne '.') {
    $cfg = Read-LaForgeConfig -Path $ConfigPath
    Start-LaForgeSession -Config $cfg -RelayFile $RelayFilePath -IsDryRun:$DryRun
}
