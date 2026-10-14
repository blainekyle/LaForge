"""
LaForge Phase 6 Integration Test: Root Explorer LVL Interception & Signature Spoofing Verification.

1. Provisions / boots the headless Android 14 (API 34) AVD (`LaForge_x86_64_API34`).
2. Applies the build-time AOSP `services.jar` signature spoofing patch (`runtime/gms/services_patched.jar`)
   BEFORE microG is injected via ADB.
3. Injects `microg-gms-core.apk` (`com.google.android.gms`) and `vending-licensing-stub.apk` (`com.android.vending`).
4. Runs the diagnostic check (`adb shell top -b -n 1 -m 5` + `adb logcat -d`) to confirm:
   - Signature spoofing (`LaForgeSpoof`) is active for `com.google.android.gms` and `com.android.vending`.
   - Zero `SecurityException` crash loops on `com.google.android.gms` / `PackageManager`.
5. Installs `tests/test_assets/root_explorer.apk` (from `samples/root-explorer-5-3-5.apk`) and the
   `com.speedsoftware.rootexplorer` LVL verification shim.
6. Launches via:
   `adb shell monkey -p com.speedsoftware.rootexplorer -c android.intent.category.LAUNCHER 1`
7. Polls Logcat to assert that `ILicensingService.checkLicense` intercepts the request and returns
   `0x0001` (`NOT_LICENSED`), gracefully failing the license check without crashing (`FATAL EXCEPTION`).
"""
from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT))

from laforge_poc import (  # noqa: E402
    apply_signature_spoofing_patch,
    run_gms_post_injection_diagnostics,
    wait_for_device,
)


def ensure_avd_running(adb_path: Path, emulator_path: Path, avd_name: str = "LaForge_x86_64_API34") -> None:
    res = subprocess.run([str(adb_path), "devices"], capture_output=True, text=True, check=False)
    if "device" in [line.split("\t")[1].strip() for line in res.stdout.splitlines() if "\t" in line]:
        wait_for_device(str(adb_path), timeout_sec=30)
        return

    print(f"[LVL Test] Booting headless AVD {avd_name}...")
    subprocess.Popen(
        [
            str(emulator_path),
            "-avd",
            avd_name,
            "-no-window",
            "-gpu",
            "host",
            "-no-boot-anim",
            "-allow-host-audio",
            "-feature",
            "WindowsHypervisorPlatform",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    wait_for_device(str(adb_path), timeout_sec=90)


def test_root_explorer_lvl_interception() -> None:
    adb = REPO_ROOT / "runtime/platform-tools/adb.exe"
    emulator = REPO_ROOT / "runtime/emulator/emulator.exe"
    patched_services = REPO_ROOT / "runtime/gms/services_patched.jar"
    microg_apk = REPO_ROOT / "runtime/gms/microg-gms-core.apk"
    vending_apk = REPO_ROOT / "runtime/gms/vending-licensing-stub.apk"
    shim_apk = REPO_ROOT / "runtime/gms/root-explorer-lvl-client.apk"

    root_explorer_apk = REPO_ROOT / "tests/test_assets/root_explorer.apk"
    if not root_explorer_apk.is_file():
        sample_candidates = list((REPO_ROOT / "samples").glob("*root*explorer*.apk"))
        if sample_candidates:
            root_explorer_apk.parent.mkdir(parents=True, exist_ok=True)
            root_explorer_apk.write_bytes(sample_candidates[0].read_bytes())

    assert root_explorer_apk.is_file(), f"Missing Root Explorer APK at {root_explorer_apk}"
    assert patched_services.is_file(), f"Missing patched services.jar at {patched_services}"
    assert microg_apk.is_file(), f"Missing microG APK at {microg_apk}"
    assert vending_apk.is_file(), f"Missing Vending Stub APK at {vending_apk}"

    # 1. Boot AVD & Apply Signature Spoofing Patch BEFORE microG injection
    ensure_avd_running(adb, emulator)
    apply_signature_spoofing_patch(str(adb), str(patched_services))

    # 2. Inject microG Services Core + Custom Vending Stub (com.android.vending)
    print("[LVL Test] Injecting microG Services Core and Vending LVL Stub...")
    subprocess.run([str(adb), "install", "-r", "-g", "--force-queryable", str(microg_apk)], check=True)
    subprocess.run([str(adb), "install", "-r", "-g", "--force-queryable", str(vending_apk)], check=True)

    # 3. Diagnostic Check (CPU top 5 + Logcat SecurityException / LaForgeSpoof check)
    diag = run_gms_post_injection_diagnostics(str(adb))
    assert len(diag["security_exceptions"]) == 0, (
        f"Unexpected GMS signature SecurityExceptions detected: {diag['security_exceptions']}"
    )

    # 4. Install Root Explorer APK (and com.speedsoftware.rootexplorer LVL shim if present)
    print(f"[LVL Test] Installing {root_explorer_apk.name}...")
    subprocess.run([str(adb), "install", "-r", "-g", str(root_explorer_apk)], check=True)
    if shim_apk.is_file():
        subprocess.run([str(adb), "install", "-r", "-g", str(shim_apk)], check=True)

    # 5. Clear logcat and launch via monkey
    subprocess.run([str(adb), "shell", "logcat", "-c"], check=True)
    monkey_cmd = [
        str(adb),
        "shell",
        "monkey",
        "-p",
        "com.speedsoftware.rootexplorer",
        "-c",
        "android.intent.category.LAUNCHER",
        "1",
    ]
    print(f"[LVL Test] Executing: {' '.join(monkey_cmd)}")
    monkey_res = subprocess.run(monkey_cmd, capture_output=True, text=True, check=True)
    assert "Events injected: 1" in monkey_res.stdout, f"Monkey launch failed: {monkey_res.stdout}"

    # 6. Poll Logcat for ILicensingService interception and 0x0001 (NOT_LICENSED) response
    intercepted_line = ""
    not_licensed_line = ""
    fatal_crash = ""

    deadline = time.time() + 15.0
    while time.time() < deadline:
        log_out = subprocess.run(
            [str(adb), "shell", "logcat", "-d"],
            capture_output=True,
            text=True,
            check=False,
        ).stdout

        for line in log_out.splitlines():
            if "LaForgeVendingStub" in line and "ILicensingService.checkLicense intercepted" in line:
                intercepted_line = line.strip()
            if ("LaForgeVendingStub" in line or "RootExplorerLVL" in line) and "0x0001" in line and "NOT_LICENSED" in line:
                not_licensed_line = line.strip()
            if "FATAL EXCEPTION" in line and (
                "com.speedsoftware.rootexplorer" in log_out or "com.clearvisions.explorer" in log_out
            ):
                fatal_crash = line.strip()

        if intercepted_line and not_licensed_line:
            break
        time.sleep(1.0)

    print(f"[LVL Test] Intercept Log   : {intercepted_line}")
    print(f"[LVL Test] Callback Result : {not_licensed_line}")

    assert not fatal_crash, f"App crashed with FATAL EXCEPTION during LVL check: {fatal_crash}"
    assert intercepted_line, "Did not observe ILicensingService.checkLicense interception in Logcat."
    assert not_licensed_line, "Did not observe 0x0001 (NOT_LICENSED) response in Logcat."
    print("[LVL Test] PASS: Root Explorer LVL check intercepted and returned 0x0001 (NOT_LICENSED) gracefully.")


if __name__ == "__main__":
    test_root_explorer_lvl_interception()
