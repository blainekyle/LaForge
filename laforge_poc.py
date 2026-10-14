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


def execute_gms_preflight_injection(config: dict, force_reconcile: bool = False) -> None:
    """
    Silently push/install microG Services Core and Custom Vending Stub (`com.android.vending`)
    during the initial emulator environment setup (or uninstall them if toggled off).
    """
    adb = config["runtime"]["adb_path"]
    gms_cfg = config.get("gms_interop", {})
    inject_microg = gms_cfg.get("inject_microg", True)
    inject_vending = gms_cfg.get("inject_vending_stub", True)

    microg_apk = Path(gms_cfg.get("microg_apk_path", "runtime/gms/microg-gms-core.apk"))
    vending_apk = Path(gms_cfg.get("vending_stub_apk_path", "runtime/gms/vending-licensing-stub.apk"))

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
    elif not inject_vending:
        if is_package_installed(adb, "com.android.vending"):
            subprocess.run([adb, "uninstall", "com.android.vending"], check=False)


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

