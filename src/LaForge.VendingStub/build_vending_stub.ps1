<#
.SYNOPSIS
    Compiles and signs the headless com.android.vending LVL stub APK (vending-licensing-stub.apk).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot    = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runtimeRoot = Join-Path $repoRoot "runtime"
$buildTools  = Join-Path $runtimeRoot "build-tools\34.0.0"
$platformJar = Join-Path $runtimeRoot "platforms\android-34\android.jar"
$fwAidl      = Join-Path $runtimeRoot "platforms\android-34\framework.aidl"

$jdkDir = Get-ChildItem "C:\Program Files\Microsoft" -Directory -Filter "jdk-*" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($jdkDir) {
    $env:JAVA_HOME = $jdkDir.FullName
    $env:Path = "$($jdkDir.FullName)\bin;$env:Path"
}

$aidlExe      = Join-Path $buildTools "aidl.exe"
$aapt2Exe     = Join-Path $buildTools "aapt2.exe"
$d8Bat        = Join-Path $buildTools "d8.bat"
$zipalignExe  = Join-Path $buildTools "zipalign.exe"
$apksignerBat = Join-Path $buildTools "apksigner.bat"

$outBuildDir = Join-Path $PSScriptRoot "build"
if (Test-Path $outBuildDir) { Remove-Item $outBuildDir -Recurse -Force }
$genJavaDir  = Join-Path $outBuildDir "gen"
$classesDir  = Join-Path $outBuildDir "classes"
$dexDir      = Join-Path $outBuildDir "dex"
New-Item -ItemType Directory -Path $genJavaDir, $classesDir, $dexDir -Force | Out-Null

$aidlRoot = Join-Path $PSScriptRoot "aidl"
Write-Host "[1/5] Generating Java Binder stubs from AIDL interfaces..." -ForegroundColor Cyan
& $aidlExe "-p$fwAidl" "-I$aidlRoot" "-o$genJavaDir" (Join-Path $aidlRoot "com\android\vending\licensing\ILicenseResultListener.aidl")
if ($LASTEXITCODE -ne 0) { throw "aidl failed on ILicenseResultListener.aidl" }
& $aidlExe "-p$fwAidl" "-I$aidlRoot" "-o$genJavaDir" (Join-Path $aidlRoot "com\android\vending\licensing\ILicensingService.aidl")
if ($LASTEXITCODE -ne 0) { throw "aidl failed on ILicensingService.aidl" }

Write-Host "[2/5] Compiling Java sources against android-34..." -ForegroundColor Cyan
$javaFiles = @()
$javaFiles += Get-ChildItem $genJavaDir -Recurse -Filter "*.java" | Select-Object -ExpandProperty FullName
$javaFiles += Get-ChildItem (Join-Path $PSScriptRoot "java") -Recurse -Filter "*.java" | Select-Object -ExpandProperty FullName
& javac -source 8 -target 8 -bootclasspath $platformJar -classpath $platformJar -d $classesDir $javaFiles
if ($LASTEXITCODE -ne 0) { throw "javac compilation failed" }

Write-Host "[3/5] Compiling JVM bytecode to Dalvik classes.dex via d8..." -ForegroundColor Cyan
$classFiles = Get-ChildItem $classesDir -Recurse -Filter "*.class" | Select-Object -ExpandProperty FullName
& $d8Bat --lib $platformJar --min-api 21 --output $dexDir $classFiles
if ($LASTEXITCODE -ne 0) { throw "d8 dexing failed" }

Write-Host "[4/5] Linking binary AndroidManifest.xml + classes.dex via aapt2..." -ForegroundColor Cyan
$unsignedApk = Join-Path $outBuildDir "vending-unsigned.apk"
$alignedApk  = Join-Path $outBuildDir "vending-aligned.apk"
& $aapt2Exe link -o $unsignedApk -I $platformJar --manifest (Join-Path $PSScriptRoot "AndroidManifest.xml")
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed" }

& jar -uf $unsignedApk -C $dexDir classes.dex
if ($LASTEXITCODE -ne 0) { throw "jar update failed" }

& $zipalignExe -f -p 4 $unsignedApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw "zipalign failed" }

Write-Host "[5/5] Signing vending-licensing-stub.apk (v1 + v2 + v3 schemes)..." -ForegroundColor Cyan
$gmsDir = Join-Path $runtimeRoot "gms"
New-Item -ItemType Directory -Path $gmsDir -Force | Out-Null
$keystorePath = Join-Path $gmsDir "laforge-stub.jks"
if (-not (Test-Path $keystorePath)) {
    & keytool -genkeypair -v -keystore $keystorePath -alias laforgestub -keyalg RSA -keysize 2048 -validity 10000 -storepass laforge123 -keypass laforge123 -dname "CN=LaForge Vending Stub, OU=Engineering, O=LaForge, L=Austin, ST=TX, C=US"
}

$finalApk = Join-Path $gmsDir "vending-licensing-stub.apk"
& $apksignerBat sign --ks $keystorePath --ks-key-alias laforgestub --ks-pass pass:laforge123 --key-pass pass:laforge123 --out $finalApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw "apksigner failed" }

Write-Host "[SUCCESS] Built signed Vending LVL Stub APK: $finalApk ($((Get-Item $finalApk).Length) bytes)" -ForegroundColor Green

# Also compile the Root Explorer LVL verification shim (com.speedsoftware.rootexplorer)
$shimDir = Join-Path $PSScriptRoot "rootexplorer_shim"
if (Test-Path $shimDir) {
    Write-Host "[Bonus] Compiling Root Explorer LVL Test Shim (com.speedsoftware.rootexplorer)..." -ForegroundColor Cyan
    $shimClassesDir = Join-Path $outBuildDir "shim_classes"
    $shimDexDir     = Join-Path $outBuildDir "shim_dex"
    New-Item -ItemType Directory -Path $shimClassesDir, $shimDexDir -Force | Out-Null

    $shimJavaFiles = @()
    $shimJavaFiles += Get-ChildItem $genJavaDir -Recurse -Filter "*.java" | Select-Object -ExpandProperty FullName
    $shimJavaFiles += Get-ChildItem (Join-Path $shimDir "java") -Recurse -Filter "*.java" | Select-Object -ExpandProperty FullName
    & javac -source 8 -target 8 -bootclasspath $platformJar -classpath $platformJar -d $shimClassesDir $shimJavaFiles
    if ($LASTEXITCODE -ne 0) { throw "javac compilation failed for rootexplorer_shim" }

    $shimClassFiles = Get-ChildItem $shimClassesDir -Recurse -Filter "*.class" | Select-Object -ExpandProperty FullName
    & $d8Bat --lib $platformJar --min-api 21 --output $shimDexDir $shimClassFiles
    if ($LASTEXITCODE -ne 0) { throw "d8 dexing failed for rootexplorer_shim" }

    $shimUnsigned = Join-Path $outBuildDir "shim-unsigned.apk"
    $shimAligned  = Join-Path $outBuildDir "shim-aligned.apk"
    & $aapt2Exe link -o $shimUnsigned -I $platformJar --manifest (Join-Path $shimDir "AndroidManifest.xml")
    if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed for rootexplorer_shim" }

    & jar -uf $shimUnsigned -C $shimDexDir classes.dex
    if ($LASTEXITCODE -ne 0) { throw "jar update failed for rootexplorer_shim" }

    & $zipalignExe -f -p 4 $shimUnsigned $shimAligned
    if ($LASTEXITCODE -ne 0) { throw "zipalign failed for rootexplorer_shim" }

    $finalShimApk = Join-Path $gmsDir "root-explorer-lvl-client.apk"
    & $apksignerBat sign --ks $keystorePath --ks-key-alias laforgestub --ks-pass pass:laforge123 --key-pass pass:laforge123 --out $finalShimApk $shimAligned
    if ($LASTEXITCODE -ne 0) { throw "apksigner failed for rootexplorer_shim" }

    Write-Host "[SUCCESS] Built signed Root Explorer LVL Shim APK: $finalShimApk ($((Get-Item $finalShimApk).Length) bytes)" -ForegroundColor Green
}

