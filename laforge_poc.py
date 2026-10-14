"""
LaForge Phase 1 Proof-of-Concept Orchestrator (Python Implementation)
Matches config.json and laforge_poc.ps1 behavior for cross-runtime compatibility.
"""
import argparse
import json
import os
import subprocess
import time
from pathlib import Path


def read_config(config_path: str) -> dict:
    path = Path(config_path)
    if not path.is_file():
        raise FileNotFoundError(f"Configuration file not found: {config_path}")
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def validate_window_boundaries(win_cfg: dict) -> dict:
    width = int(win_cfg["width"])
    height = int(win_cfg["height"])
    x = int(win_cfg["x"])
    y = int(win_cfg["y"])

    if not (200 <= width <= 7680):
        raise ValueError(f"Invalid window width ({width}). Must be between 200 and 7680.")
    if not (200 <= height <= 4320):
        raise ValueError(f"Invalid window height ({height}). Must be between 200 and 4320.")
    if not (-3840 <= x <= 7680 and -2160 <= y <= 4320):
        raise ValueError(f"Window coordinates ({x}, {y}) are out of valid desktop bounds.")

    return {
        "x": x,
        "y": y,
        "width": width,
        "height": height,
        "borderless": bool(win_cfg.get("borderless", False)),
    }


def build_emulator_command(config: dict) -> list[str]:
    rt = config["runtime"]
    cmd = [rt["emulator_path"], "-avd", rt["avd_name"]]
    if rt.get("headless", True):
        cmd.append("-no-window")
    cmd.extend(["-gpu", rt.get("gpu_mode", "host")])
    if rt.get("snapshot"):
        cmd.extend(["-snapshot", rt["snapshot"]])
    cmd.extend(["-no-boot-anim", "-no-audio"])
    if rt.get("whpx", False):
        cmd.extend(["-feature", "WindowsHypervisorPlatform"])
    return cmd


def build_scrcpy_command(config: dict) -> list[str]:
    disp = config["display"]
    bounds = validate_window_boundaries(disp["window"])
    cmd = [
        disp["scrcpy_path"],
        f"--window-title={config['app']['title']}",
        f"--window-x={bounds['x']}",
        f"--window-y={bounds['y']}",
        f"--window-width={bounds['width']}",
        f"--window-height={bounds['height']}",
        f"--render-driver={disp.get('render_driver', 'direct3d')}",
        f"--max-fps={disp.get('max_fps', 60)}",
    ]
    if bounds["borderless"]:
        cmd.append("--window-borderless")
    if disp["window"].get("always_on_top"):
        cmd.append("--always-on-top")
    if disp["window"].get("fullscreen"):
        cmd.append("--fullscreen")
    if disp.get("stay_awake"):
        cmd.append("--stay-awake")
    return cmd


def build_apk_launch_command(config: dict) -> list[str]:
    adb = config["runtime"]["adb_path"]
    component = f"{config['app']['package_name']}/{config['app']['activity_name']}"
    return [adb, "shell", "am", "start", "-W", "-n", component]


def build_relay_commands(config: dict, host_file: str) -> tuple[list[str], list[str]]:
    host_path = Path(host_file).resolve()
    if not host_path.is_file():
        raise FileNotFoundError(f"Relay host file does not exist: {host_file}")

    ext = host_path.suffix.lower()
    associations = config["relays"]["associations"]
    if ext not in associations:
        raise ValueError(f"Unsupported file extension '{ext}' for relay.")

    rule = associations[ext]
    remote_dir = config["relays"]["remote_staging_dir"].rstrip("/")
    remote_path = f"{remote_dir}/{host_path.name}"
    remote_uri = f"file://{remote_path}"
    component = f"{config['app']['package_name']}/{config['app']['activity_name']}"
    adb = config["runtime"]["adb_path"]

    push_cmd = [adb, "push", str(host_path), remote_path]
    intent_cmd = [
        adb,
        "shell",
        "am",
        "start",
        "-a",
        rule["action"],
        "-d",
        remote_uri,
        "-t",
        rule["mime_type"],
        "-n",
        component,
    ]
    return push_cmd, intent_cmd


def wait_for_device(adb_path: str, timeout_sec: int = 60) -> None:
    subprocess.run([adb_path, "wait-for-device"], check=True)
    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        output = subprocess.check_output(
            [adb_path, "shell", "getprop", "sys.boot_completed"], text=True
        ).strip()
        if output == "1":
            return
        time.sleep(1)
    raise TimeoutError(f"Android runtime failed to boot within {timeout_sec}s.")


# ==============================================================================
# Phase 6: GMS & Licensing Interop (microG + Custom Vending LVL Stub)
# ==============================================================================

EXCLUDED_INTEROP_PACKAGES: frozenset[str] = frozenset(
    {
        "com.google.android.gms",
        "com.mgoogle.android.gms",
        "org.microg.gms",
        "org.microg.gms.droidguard",
        "app.revanced.android.gms",
        "com.google.android.gsf",
        "com.mgoogle.android.gsf",
        "com.android.vending",
    }
)


def is_excluded_interop_package(package_name: str) -> bool:
    """Return True if package_name belongs to microG / GMS / Vending interop stubs."""
    pkg = (package_name or "").strip().lower()
    if not pkg:
        return False
    return pkg in EXCLUDED_INTEROP_PACKAGES or pkg.startswith("org.microg.")


def filter_user_facing_packages(packages: list[str]) -> list[str]:
    """Filter out microG Services Core and Custom Vending Stub from user-facing app lists."""
    return [pkg for pkg in packages if not is_excluded_interop_package(pkg)]


def is_package_installed(adb_path: str, package_name: str) -> bool:
    """Check via ADB whether a given Android package is already installed on the guest."""
    res = subprocess.run(
        [adb_path, "shell", "pm", "path", package_name],
        capture_output=True,
        text=True,
        check=False,
    )
    return res.returncode == 0 and "package:" in (res.stdout or "")


def build_gms_preflight_commands(config: dict) -> list[list[str]]:
    """
    Build the ADB install commands for the microG Services Core APK and the
    Custom Vending LVL Stub APK (`com.android.vending`) using `--force-queryable`
    so Android 11+ (API 30+) client apps can bind to ILicensingService.
    """
    adb = config["runtime"]["adb_path"]
    gms_cfg = config.get("gms_interop", {})
    inject_microg = gms_cfg.get("inject_microg", True)
    inject_vending = gms_cfg.get("inject_vending_stub", True)

    microg_apk = gms_cfg.get("microg_apk_path", "runtime/gms/microg-gms-core.apk")
    vending_apk = gms_cfg.get("vending_stub_apk_path", "runtime/gms/vending-licensing-stub.apk")

    commands: list[list[str]] = []
    if inject_microg:
        commands.append([adb, "install", "-r", "-g", "--force-queryable", microg_apk])
    if inject_vending:
        commands.append([adb, "install", "-r", "-g", "--force-queryable", vending_apk])
    return commands


def apply_signature_spoofing_patch(
    adb_path: str,
    patched_services_jar: str = "runtime/gms/services_patched.jar",
) -> None:
    """
    Task 2: Apply the headless AOSP signature spoofing patch (`services_patched.jar`)
    BEFORE microG is injected via ADB.
    """
    jar_path = Path(patched_services_jar)
    if not jar_path.is_file():
        print(f"[LaForge Spoof] Warning: {jar_path} not found; skipping services.jar patch.")
        return

    active = subprocess.run(
        [adb_path, "shell", "getprop", "debug.laforge.sigspoof_active"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    if active == "1":
        return

    print("[LaForge Spoof] Applying AOSP services.jar signature spoofing patch before microG injection...")
    subprocess.run([adb_path, "root"], check=False, capture_output=True)
    subprocess.run([adb_path, "wait-for-device"], check=True)
    subprocess.run(
        [adb_path, "push", str(jar_path), "/data/system/laforge_services.jar"],
        check=True,
    )

    mount_cmd = (
        "setenforce 0 2>/dev/null; "
        "chmod 644 /data/system/laforge_services.jar; "
        "chown root:root /data/system/laforge_services.jar; "
        "chcon u:object_r:system_file:s0 /data/system/laforge_services.jar; "
        "umount /system/framework/services.jar 2>/dev/null; "
        "mount -o bind /data/system/laforge_services.jar /system/framework/services.jar; "
        "/apex/com.android.art/bin/dex2oat64 "
        "--dex-file=/system/framework/services.jar "
        "--oat-file=/data/system/laforge_services.odex "
        "--app-image-file=/data/system/laforge_services.art "
        "--instruction-set=x86_64 --compiler-filter=verify 2>/dev/null || true; "
        "chmod 644 /data/system/laforge_services.*; "
        "chown root:root /data/system/laforge_services.*; "
        "chcon u:object_r:system_file:s0 /data/system/laforge_services.*; "
        "umount /system/framework/oat/x86_64/services.art 2>/dev/null; "
        "umount /system/framework/oat/x86_64/services.odex 2>/dev/null; "
        "umount /system/framework/oat/x86_64/services.vdex 2>/dev/null; "
        "mount -o bind /data/system/laforge_services.art /system/framework/oat/x86_64/services.art; "
        "mount -o bind /data/system/laforge_services.odex /system/framework/oat/x86_64/services.odex; "
        "mount -o bind /data/system/laforge_services.vdex /system/framework/oat/x86_64/services.vdex; "
        "setprop sys.boot_completed 0; stop; start"
    )
    subprocess.run([adb_path, "shell", mount_cmd], check=True)
    wait_for_device(adb_path, timeout_sec=60)
    subprocess.run(
        [adb_path, "shell", "setprop", "debug.laforge.sigspoof_active", "1"],
        check=False,
    )


def run_gms_post_injection_diagnostics(adb_path: str) -> dict:
    """
    Task 1: Diagnostic routine after injecting microG.
    Polls `adb shell top -b -n 1 -m 5` and `adb logcat -d` for recurring
    `SecurityException` tied to `com.google.android.gms` or `PackageManager`
    rejecting signature claims, as well as `LaForgeSpoof` verification entries.
    """
    top_res = subprocess.run(
        [adb_path, "shell", "top", "-b", "-n", "1", "-m", "5"],
        capture_output=True,
        text=True,
        check=False,
    )
    top_output = (top_res.stdout or "").strip()

    log_res = subprocess.run(
        [
            adb_path,
            "shell",
            "logcat -d -t 500 | grep -iE 'SecurityException|com\\.google\\.android\\.gms|PackageManager|FAKE_PACKAGE_SIGNATURE|LaForgeSpoof|LaForgeVendingStub'",
        ],
        capture_output=True,
        text=True,
        check=False,
    )
    log_lines = [line.strip() for line in (log_res.stdout or "").splitlines() if line.strip()]
    sec_exceptions = [
        line
        for line in log_lines
        if "SecurityException" in line
        and ("com.google.android.gms" in line or "PackageManager" in line or "signature" in line.lower())
    ]
    spoof_hits = [line for line in log_lines if "LaForgeSpoof" in line]

    print("\n================ [LaForge GMS Diagnostic Check] ================")
    print("[1/2] Top 5 CPU Processes (adb shell top -b -n 1 -m 5):")
    print(top_output)
    print(f"\n[2/2] Signature Spoofing Events: {len(spoof_hits)} | GMS SecurityExceptions: {len(sec_exceptions)}")
    for line in spoof_hits[-5:]:
        print(f"  [SPOOF OK] {line}")
    for line in sec_exceptions[-5:]:
        print(f"  [SEC-EX]   {line}")
    print("================================================================\n")

    return {
        "top_output": top_output,
        "security_exceptions": sec_exceptions,
        "spoof_events": spoof_hits,
    }


def execute_gms_preflight_injection(config: dict, force_reconcile: bool = False) -> None:
    """
    Apply signature spoofing patch FIRST, then silently push/install microG Services Core
    and Custom Vending Stub (`com.android.vending`), followed by diagnostic verification.
    """
    adb = config["runtime"]["adb_path"]
    gms_cfg = config.get("gms_interop", {})
    inject_microg = gms_cfg.get("inject_microg", True)
    inject_vending = gms_cfg.get("inject_vending_stub", True)

    microg_apk = Path(gms_cfg.get("microg_apk_path", "runtime/gms/microg-gms-core.apk"))
    vending_apk = Path(gms_cfg.get("vending_stub_apk_path", "runtime/gms/vending-licensing-stub.apk"))
    patched_jar = gms_cfg.get("patched_services_jar_path", "runtime/gms/services_patched.jar")

    # 0. Apply AOSP Signature Spoofing Patch BEFORE injecting microG
    if inject_microg or inject_vending:
        apply_signature_spoofing_patch(adb, patched_jar)

    # 1. Reconcile microG Services Core
    if inject_microg and microg_apk.is_file():
        installed = (
            is_package_installed(adb, "com.google.android.gms")
            or is_package_installed(adb, "com.mgoogle.android.gms")
        )
        if not installed or force_reconcile:
            subprocess.run(
                [adb, "install", "-r", "-g", "--force-queryable", str(microg_apk)],
                check=True,
            )
        for pkg in ("com.google.android.gms", "com.mgoogle.android.gms"):
            if is_package_installed(adb, pkg):
                subprocess.run(
                    [adb, "shell", "dumpsys", "deviceidle", "whitelist", f"+{pkg}"],
                    check=False,
                )
                subprocess.run(
                    [adb, "shell", "pm", "grant", pkg, "android.permission.FAKE_PACKAGE_SIGNATURE"],
                    check=False,
                )
        if not is_package_installed(adb, "com.google.android.gsf"):
            gsf_apk = microg_apk.parent / "gsf-proxy.apk"
            if gsf_apk.is_file():
                subprocess.run([adb, "install", "-r", "-g", str(gsf_apk)], check=False)
        for perm in ("POST_NOTIFICATIONS", "GET_ACCOUNTS", "READ_PHONE_STATE", "ACCESS_FINE_LOCATION",
                     "ACCESS_COARSE_LOCATION", "ACCESS_BACKGROUND_LOCATION", "READ_CONTACTS"):
            subprocess.run(
                [adb, "shell", "pm", "grant", "com.google.android.gms", f"android.permission.{perm}"],
                check=False,
            )
    elif not inject_microg:
        for pkg in ("com.google.android.gms", "com.mgoogle.android.gms", "org.microg.gms"):
            if is_package_installed(adb, pkg):
                subprocess.run([adb, "uninstall", pkg], check=False)

    # 2. Reconcile Custom Vending LVL Stub (com.android.vending -> 0x0001 NOT_LICENSED)
    if inject_vending and vending_apk.is_file():
        if not is_package_installed(adb, "com.android.vending") or force_reconcile:
            subprocess.run(
                [adb, "install", "-r", "-g", "--force-queryable", str(vending_apk)],
                check=True,
            )
            subprocess.run(
                [adb, "shell", "pm", "grant", "com.android.vending", "android.permission.FAKE_PACKAGE_SIGNATURE"],
                check=False,
            )
    elif not inject_vending:
        if is_package_installed(adb, "com.android.vending"):
            subprocess.run([adb, "uninstall", "com.android.vending"], check=False)

    # 3. Run Post-Injection Diagnostic Check (CPU top 5 + Logcat SecurityException / Spoof check)
    run_gms_post_injection_diagnostics(adb)


def build_microg_settings_intent_command(
    adb_path: str,
    package_name: str = "com.google.android.gms",
    display_id: int | None = None,
) -> list[str]:
    """Build the ADB command to launch the microG Settings activity directly."""
    cmd = [adb_path, "shell", "am", "start", "-W"]
    if display_id is not None and display_id > 0:
        cmd.extend(["--display", str(display_id)])
    cmd.extend(["-n", f"{package_name}/org.microg.gms.ui.SettingsActivity"])
    return cmd


def build_clear_microg_data_command(
    adb_path: str, package_name: str = "com.google.android.gms"
) -> list[str]:
    """Build the ADB command to clear microG Services Core application data."""
    return [adb_path, "shell", "pm", "clear", package_name]


def main() -> None:
    parser = argparse.ArgumentParser(description="LaForge Phase 1-6 Orchestrator")
    parser.add_argument("--config", default="config.json", help="Path to config.json")
    parser.add_argument("--relay-file", default="", help="Optional host file to relay")
    parser.add_argument("--dry-run", action="store_true", help="Print commands without executing")
    parser.add_argument("--launch-microg-settings", action="store_true", help="Launch microG Settings")
    parser.add_argument("--clear-microg-data", action="store_true", help="Clear microG app data")
    args = parser.parse_args()

    config = read_config(args.config)
    adb_path = config["runtime"]["adb_path"]

    if args.launch_microg_settings:
        cmd = build_microg_settings_intent_command(adb_path)
        print(f"[LaForge] microG Settings : {' '.join(cmd)}")
        if not args.dry_run:
            subprocess.run(cmd, check=True)
        return

    if args.clear_microg_data:
        cmd = build_clear_microg_data_command(adb_path)
        print(f"[LaForge] Clear microG    : {' '.join(cmd)}")
        if not args.dry_run:
            subprocess.run(cmd, check=True)
        return

    emu_cmd = build_emulator_command(config)
    gms_cmds = build_gms_preflight_commands(config)
    scrcpy_cmd = build_scrcpy_command(config)
    launch_cmd = build_apk_launch_command(config)

    print(f"[LaForge] Emulator Cmd : {' '.join(emu_cmd)}")
    for idx, gcmd in enumerate(gms_cmds, start=1):
        print(f"[LaForge] GMS PreFlight #{idx}: {' '.join(gcmd)}")
    print(f"[LaForge] Scrcpy Cmd   : {' '.join(scrcpy_cmd)}")
    print(f"[LaForge] APK Launch   : {' '.join(launch_cmd)}")

    if not args.dry_run:
        subprocess.Popen(emu_cmd)
        wait_for_device(adb_path, config["runtime"].get("boot_timeout_sec", 60))
        execute_gms_preflight_injection(config)
        subprocess.Popen(scrcpy_cmd)
        subprocess.run(launch_cmd, check=True)

    if args.relay_file:
        push_cmd, intent_cmd = build_relay_commands(config, args.relay_file)
        print(f"[LaForge] Relay Push   : {' '.join(push_cmd)}")
        print(f"[LaForge] Relay Intent : {' '.join(intent_cmd)}")
        if not args.dry_run:
            subprocess.run(push_cmd, check=True)
            subprocess.run(intent_cmd, check=True)


if __name__ == "__main__":
    main()

