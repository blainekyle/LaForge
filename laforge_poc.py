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


def main() -> None:
    parser = argparse.ArgumentParser(description="LaForge Phase 1 POC")
    parser.add_argument("--config", default="config.json", help="Path to config.json")
    parser.add_argument("--relay-file", default="", help="Optional host file to relay")
    parser.add_argument("--dry-run", action="store_true", help="Print commands without executing")
    args = parser.parse_args()

    config = read_config(args.config)
    emu_cmd = build_emulator_command(config)
    scrcpy_cmd = build_scrcpy_command(config)
    launch_cmd = build_apk_launch_command(config)

    print(f"[LaForge] Emulator Cmd : {' '.join(emu_cmd)}")
    print(f"[LaForge] Scrcpy Cmd   : {' '.join(scrcpy_cmd)}")
    print(f"[LaForge] APK Launch   : {' '.join(launch_cmd)}")

    if not args.dry_run:
        subprocess.Popen(emu_cmd)
        wait_for_device(config["runtime"]["adb_path"], config["runtime"].get("boot_timeout_sec", 60))
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
