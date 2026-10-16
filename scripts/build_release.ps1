<#
.SYNOPSIS
    Builds launcher.exe / LaForgeConsole.exe and LaForgeSetup.exe (single-file) and refreshes the installer payload.
.PARAMETER Deploy
    Also copy the fresh binaries to the repo root, dist\ (Console + forged bundles) and an existing %APPDATA%\LaForge install.
#>
[CmdletBinding()]
param([switch]$Deploy)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repo "publish"
$payload = Join-Path $repo "src\LaForge.Installer\installer_payload.zip"

Write-Host "== Publishing launcher ==" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\LaForge.Launcher\LaForge.Launcher.csproj") -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o (Join-Path $stage "launcher") | Select-Object -Last 2
$launcher = Join-Path $stage "launcher\launcher.exe"

Write-Host "== Refreshing installer payload ==" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not (Test-Path $payload)) { throw "installer_payload.zip is missing; it holds bridge\, img\, runtime\gms\ and runtime\arm64\." }
$zip = [System.IO.Compression.ZipFile]::Open($payload, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($name in "LaForgeConsole.exe", "launcher.exe") {
        $e = $zip.GetEntry($name); if ($e) { $e.Delete() }
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $launcher, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    $arm = "runtime/arm64/ndk_translation_arm64_api34.tar.gz"
    if (-not $zip.GetEntry($arm)) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $repo "runtime\arm64\ndk_translation_arm64_api34.tar.gz"), $arm, [System.IO.Compression.CompressionLevel]::NoCompression)
    }
} finally { $zip.Dispose() }

Write-Host "== Publishing installer ==" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\LaForge.Installer\LaForge.Installer.csproj") -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o (Join-Path $stage "installer") | Select-Object -Last 2
$setup = Join-Path $stage "installer\LaForgeSetup.exe"

if ($Deploy) {
    Write-Host "== Deploying ==" -ForegroundColor Cyan
    Get-Process LaForgeConsole, launcher -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($t in "launcher.exe", "LaForgeConsole.exe", "dist\LaForgeConsole.exe") { Copy-Item $launcher (Join-Path $repo $t) -Force }
    Get-ChildItem (Join-Path $repo "dist") -Directory | ForEach-Object {
        if (Test-Path (Join-Path $_.FullName "launcher.exe")) {
            Get-ChildItem $_.FullName -Filter *.exe | ForEach-Object { Copy-Item $launcher $_.FullName -Force }
        }
    }
    Copy-Item $setup (Join-Path $repo "LaForgeSetup.exe") -Force
    Copy-Item $setup (Join-Path $repo "dist\LaForgeSetup.exe") -Force
}
Write-Host "Launcher : $launcher"
Write-Host "Installer: $setup"
