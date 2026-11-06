<table>
  <tr>
    <td align="left" valign="middle">
      <h1>LaForge</h1>
      <p><strong>Turn Android APKs into standalone Windows desktop applications.</strong><br>
      Shared-pool multi-display virtualization, desktop notification polling, background window persistence, and default LAN access—without terminal clutter.</p>
    </td>
    <td align="center" valign="middle" width="30%">
      <img src="img/laforge_splash.png" alt="LaForge" height="180" />
    </td>
  </tr>
</table>

---

## Key Features

- **Native Windows Desktop Shortcuts & Windows**: Each forged APK gets its own dedicated Windows `.exe` launcher, custom icon, Start Menu / Desktop shortcuts, and native window border—running side-by-side like standard desktop programs.
- **Shared Runtime Pool & Per-App Virtual Displays**: Forged apps run inside a **shared Android 14 (`API 34`) runtime pool** by default rather than spinning up separate virtual machines per app. Each open app is assigned its own hardware-accelerated **SDL3 / Direct3D virtual display (`--new-display`)** so multiple apps can run simultaneously in separate desktop windows while sharing one headless emulator instance.
- **Automatic Emulator Shutdown & Persistent Background Windows**:
  - **Idle Emulator Shutdown & Snapshot Boot**: When all forged app windows are closed (and the Console is not holding a session open), LaForge shuts down the headless emulator process (`adb emu kill`) so it does not sit idle in the background, and boots from a saved `default_boot` snapshot on the next app launch.
  - **Per-App Background Mode**: Individual apps can be configured to stay running in the background when closed (`BackgroundOnClose`) or toggled directly from the Console. Backgrounding hides the desktop `scrcpy` window while keeping the app and its virtual display active (`--no-vd-destroy-content`, plus `deviceidle whitelist` Doze exemption), allowing the existing window to be un-hidden when re-opened without cold-starting the app.
- **Console Notification Center & microG Compatibility**:
  - **Console Notification Center**: While open, `LaForge Console` polls active Android system notifications via ADB (`dumpsys notification`), displaying them in a centralized list view with per-app unread badges, notification title/body/channel columns, and a **Clear All Notifications** action (while hiding status-bar notification icons inside individual app windows).
  - **microG & Licensing Stub**: Includes an AOSP signature-spoofing patch (`services_patched.jar` / `LaForgeSpoof`), **microG (`GmsCore`)**, automatic `POST_NOTIFICATIONS` permission grants, and a Play Licensing stub (`LaForge.VendingStub`) for apps that rely on basic Google Play Services or cloud-messaging registration (subject to standard `microG` and Google Play Integrity limitations).
- **Joined to Your Local Network (LAN) by Default**: Managed apps share your host LAN network context by default so they can communicate with local IP cameras, NVRs, smart-home devices, and local servers. If you want to block an app's network access, LaForge Console provides a per-app UID firewall toggle (`iptables` / `netpolicy`) alongside per-app Audio, Microphone, and HID input locks.
- **ARM64 Translation Built-In**: Integrated with Google `libndk_translation` (`v0.2.3`), allowing 64-bit ARM (`arm64-v8a`) Android apps to run on `x86_64` Windows PCs.
- **Host Hardware & Filesystem Bridges**: Built-in host webcam passthrough (configurable Front/Rear cameras), microphone bridge (`hostmicon`), raw PCM audio routing, UHID gamepad passthrough, GPS geolocation bridge (`adb emu geo fix`), and a two-way `/sdcard/HOST` ↔ `%LOCALAPPDATA%\LaForge\HOST` shared folder.
- **Clean Uninstaller**: Full integration with Windows Installed Apps (`Add or Remove Programs`), Start Menu cleanup, and optional removal of forged app launchers and runtime images.

---

## App in Action

### 1. LaForge Console & Notification Center
*LaForge Central Console displaying managed apps, live foreground/background telemetry, per-app policy toggles, and the Android Notification Center.*  
![LaForge Console](img/screenshots/01-console-dashboard.png)

### 2. "Forge New APK..." Dialog
*A populated "Forge New APK..." dialog with automatic package inspection, ABI detection, virtual display configuration, and host folder sharing.*  
![Populated Forge New APK Dialog](img/screenshots/02-forge-dialog.png)

### 3. VLC Running in LaForge
*VLC for Android executing inside a native desktop window with direct audio and input routing.*  
![VLC running in LaForge](img/screenshots/03-vlc-running.png)

### 4. Branded Loading Splash Screen
*Patrickric's ConnectFour Studio launching with splash screen overlay while allocating the Direct3D virtual display.*  
![ConnectFour Studio Launching Splash](img/screenshots/04-connectfour-splash.png)

### 5. Multi-Window Multitasking & Status Tracking
*The ConnectFour app running in a manually-resized portrait window beside LaForge Console showing the app's live active status.*  
![ConnectFour Studio beside LaForge Console](img/screenshots/05-connectfour-portrait-multitasking.png)

---

## Getting Started

### Installation
1. Download the latest **`LaForgeSetup.exe`** from the [Releases](https://github.com/blainekyle/LaForge/releases) page.
2. Run the setup wizard.
3. Choose whether to download the Android runtime during setup (recommended, ~1.2 GB) or let LaForge provision it automatically when you launch your first forged app.
4. Launch **LaForge Console** from your Start Menu or Desktop.

### Forging Your First App
1. Open **LaForge Console**.
2. Click **Forge APK** (or drag and drop an `.apk` file into the window).
3. Choose your desired display profile (Phone / Tablet / Freeform), orientation, and shortcut options.
4. Click **Forge!** — LaForge packages a standalone `.exe` launcher and places shortcuts on your Desktop and Start Menu.
5. Launch the app anytime like a standard Windows program, or toggle **Background** in LaForge Console if you want the app window hidden while keeping its Android process active.

---

## Architecture

```text
[ Windows Desktop & System Tray ]
        │
        ├──► [ App.exe ] (Native WinExe Stub per Forged App)
        │         │
        │         ▼
        │    [ Local Loopback SDL3 / Direct3D Virtual Display Bridge ]
        │    • Independent per-app virtual display (--new-display)
        │    • Hides/restores existing scrcpy window when backgrounded
        │
        └──► [ LaForgeConsole.exe ] (App Manager & Notification Center)
                  │
                  ▼
        [ Shared Headless Android 14 (API 34) Runtime Pool ]
        • Shuts down when no apps are active; boots from default_boot snapshot
        • Default Host LAN connectivity + optional per-UID iptables firewall
        • Google libndk_translation (ARM64 -> x86_64 execution)
        • AOSP Signature Spoofing + microG + VendingStub
        • Host Webcam, Mic, Raw PCM Audio, GPS & /sdcard/HOST Folder Bridges
```

### Components
- **`LaForge.Launcher`**: C# .NET 8 native `WinExe` that boots the shared headless runtime from its `default_boot` snapshot (if not already running), allocates a Direct3D virtual display, and shuts down the emulator when the last active session closes.
- **`LaForge.Console`**: WinForms control panel and system-tray host for forging APKs, monitoring foreground/background/audio states, enforcing per-app firewall/audio/mic/HID policies, configuring hardware bridges (cameras, GPS, gamepad), and polling Android notifications via ADB.
- **`LaForge.VendingStub` & `GmsInteropManager`**: Pairs AOSP `services.jar` signature spoofing (`LaForgeSpoof`) and `microG` with a Play Licensing stub and `deviceidle` whitelisting.
- **`LaForge.Installer` & `RuntimeProvisioner`**: Self-contained single-file installer wizard that provisions the pinned Android emulator (`37.2.12`), platform-tools (`37.0.1`), and Android 14 system image (`x86_64-34_r04.zip`) verified with SHA-256 integrity checks.

---

## Building from Source

### Prerequisites
- Windows 10 (1809+) or Windows 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PowerShell 7+ or Windows PowerShell 5.1

### Build Instructions
To build and publish single-file release binaries and the installer:

```powershell
# Clone the repository
git clone https://github.com/blainekyle/LaForge.git
cd LaForge

# Run the automated release build script
pwsh -ExecutionPolicy Bypass -File .\scripts\build_release.ps1 -Deploy
```

The compiled binaries will be output to:
- `publish/launcher/launcher.exe`
- `publish/installer/LaForgeSetup.exe`
- Root workspace `LaForgeConsole.exe` and `LaForgeSetup.exe`

---

## License
This project is licensed under the Apache 2.0 License. See [LICENSE](LICENSE) for details.