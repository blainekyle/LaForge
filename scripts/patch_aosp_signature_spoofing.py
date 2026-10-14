"""
LaForge AOSP Android 14 (API 34) Headless Signature Spoofing Patcher.

Patches /system/framework/services.jar (specifically classes2.dex):
  1. com/android/server/pm/parsing/PackageInfoUtils.smali:
     Injects `applyLaForgeSignatureSpoof(PackageInfo, AndroidPackage)` at the exit
     of `PackageInfoUtils.generate(...)` to spoof both `PackageInfo.signatures` (v1)
     and `PackageInfo.signingInfo` (v2/v3 SigningDetails) with the official Google
     Play certificate for `com.google.android.gms`, `com.mgoogle.android.gms`,
     `com.google.android.gsf`, `com.android.vending`, or any package declaring
     `<meta-data android:name="fake-signature" .../>`.
  2. com/android/server/pm/permission/PermissionManagerServiceImpl.smali:
     Grants `android.permission.FAKE_PACKAGE_SIGNATURE` in both `checkPermission`
     and `checkUidPermission`.

Also handles deploying `runtime/gms/services_patched.jar` into the headless AVD
via `adb root` + `/data/system/laforge_services.jar` bind-mount BEFORE microG is
injected via ADB.
"""
from __future__ import annotations

import shutil
import subprocess
import time
import zipfile
from pathlib import Path

# Official Google Play Services certificate hex string (from microG fake_signature resource)
GOOGLE_PLAY_FAKE_SIGNATURE_HEX = (
    "308204433082032ba003020102020900c2e08746644a308d300d06092a864886f70d0101040500"
    "3074310b3009060355040613025553311330110603550408130a43616c69666f726e6961311630"
    "140603550407130d4d6f756e7461696e205669657731143012060355040a130b476f6f676c6520"
    "496e632e3110300e060355040b1307416e64726f69643110300e06035504031307416e64726f69"
    "64301e170d3038303832313233313333345a170d3336303130373233313333345a3074310b3009"
    "060355040613025553311330110603550408130a43616c69666f726e6961311630140603550407"
    "130d4d6f756e7461696e205669657731143012060355040a130b476f6f676c6520496e632e3110"
    "300e060355040b1307416e64726f69643110300e06035504031307416e64726f69643082012030"
    "0d06092a864886f70d01010105000382010d00308201080282010100ab562e00d83ba208ae0a96"
    "6f124e29da11f2ab56d08f58e2cca91303e9b754d372f640a71b1dcb130967624e4656a7776a92"
    "193db2e5bfb724a91e77188b0e6a47a43b33d9609b77183145ccdf7b2e586674c9e1565b1f4c6a"
    "5955bff251a63dabf9c55c27222252e875e4f8154a645f897168c0b1bfc612eabf785769bb34aa"
    "7984dc7e2ea2764cae8307d8c17154d7ee5f64a51a44a602c249054157dc02cd5f5c0e55fbef85"
    "19fbe327f0b1511692c5a06f19d18385f5c4dbc2d6b93f68cc2979c70e18ab93866b3bd5db8999"
    "552a0e3b4c99df58fb918bedc182ba35e003c1b4b10dd244a8ee24fffd333872ab5221985edab0"
    "fc0d0b145b6aa192858e79020103a381d93081d6301d0603551d0e04160414c77d8cc221175625"
    "9a7fd382df6be398e4d786a53081a60603551d2304819e30819b8014c77d8cc2211756259a7fd3"
    "82df6be398e4d786a5a178a4763074310b3009060355040613025553311330110603550408130a"
    "43616c69666f726e6961311630140603550407130d4d6f756e7461696e20566965773114301206"
    "0355040a130b476f6f676c6520496e632e3110300e060355040b1307416e64726f69643110300e"
    "06035504031307416e64726f6964820900c2e08746644a308d300c0603551d13040530030101ff"
    "300d06092a864886f70d010104050003820101006dd252ceef85302c360aaace939bcff2cca904"
    "bb5d7a1661f8ae46b2994204d0ff4a68c7ed1a531ec4595a623ce60763b167297a7ae35712c407"
    "f208f0cb109429124d7b106219c084ca3eb3f9ad5fb871ef92269a8be28bf16d44c8d9a08e6cb2"
    "f005bb3fe2cb96447e868e731076ad45b33f6009ea19c161e62641aa99271dfd5228c5c587875d"
    "db7f452758d661f6cc0cccb7352e424cc4365c523532f7325137593c4ae341f4db41edda0d0b10"
    "71a7c440f0fe9ea01cb627ca674369d084bd2fd911ff06cdbf2cfa10dc0f893ae35762919048c7"
    "efc64c7144178342f70581c9de573af55b390dd7fdb9418631895d5f759f30112687ff621410c0"
    "69308a"
)


def patch_package_info_utils_smali(smali_path: Path) -> bool:
    """Inject applyLaForgeSignatureSpoof into PackageInfoUtils.smali."""
    text = smali_path.read_text(encoding="utf-8")
    if "applyLaForgeSignatureSpoof" in text:
        return False

    target_return = (
        "    move-object/from16 v1, p0\n\n"
        "    invoke-static {v2, v1}, Lcom/android/server/pm/parsing/PackageInfoUtils;"
        "->applyLaForgeSignatureSpoof(Landroid/content/pm/PackageInfo;"
        "Lcom/android/server/pm/pkg/AndroidPackage;)V\n\n"
        "    return-object v2\n.end method"
    )
    text = text.replace("    return-object v2\n.end method", target_return, 1)

    helper_method = f"""
.method private static applyLaForgeSignatureSpoof(Landroid/content/pm/PackageInfo;Lcom/android/server/pm/pkg/AndroidPackage;)V
    .locals 7

    if-eqz p0, :cond_done
    if-eqz p1, :cond_done

    :try_start_spoof
    invoke-interface {{p1}}, Lcom/android/server/pm/pkg/AndroidPackage;->getPackageName()Ljava/lang/String;
    move-result-object v0
    if-eqz v0, :cond_done

    const/4 v1, 0x0

    invoke-interface {{p1}}, Lcom/android/server/pm/pkg/AndroidPackage;->getMetaData()Landroid/os/Bundle;
    move-result-object v2
    if-eqz v2, :cond_check_gms

    const-string v3, "fake-signature"
    invoke-virtual {{v2, v3}}, Landroid/os/Bundle;->getString(Ljava/lang/String;)Ljava/lang/String;
    move-result-object v1

    :cond_check_gms
    if-nez v1, :cond_have_sig

    const-string v3, "com.google.android.gms"
    invoke-virtual {{v3, v0}}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v3
    if-nez v3, :cond_use_default_gms

    const-string v3, "com.mgoogle.android.gms"
    invoke-virtual {{v3, v0}}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v3
    if-nez v3, :cond_use_default_gms

    const-string v3, "com.google.android.gsf"
    invoke-virtual {{v3, v0}}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v3
    if-nez v3, :cond_use_default_gms

    const-string v3, "com.android.vending"
    invoke-virtual {{v3, v0}}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v3
    if-eqz v3, :cond_done

    :cond_use_default_gms
    const-string v1, "{GOOGLE_PLAY_FAKE_SIGNATURE_HEX}"

    :cond_have_sig
    new-instance v2, Landroid/content/pm/Signature;
    invoke-direct {{v2, v1}}, Landroid/content/pm/Signature;-><init>(Ljava/lang/String;)V

    const/4 v3, 0x1
    new-array v4, v3, [Landroid/content/pm/Signature;
    const/4 v5, 0x0
    aput-object v2, v4, v5

    iget-object v5, p0, Landroid/content/pm/PackageInfo;->signatures:[Landroid/content/pm/Signature;
    if-eqz v5, :cond_check_signing_info

    iput-object v4, p0, Landroid/content/pm/PackageInfo;->signatures:[Landroid/content/pm/Signature;

    :cond_check_signing_info
    iget-object v5, p0, Landroid/content/pm/PackageInfo;->signingInfo:Landroid/content/pm/SigningInfo;
    if-eqz v5, :cond_log

    new-instance v5, Landroid/content/pm/SigningDetails$Builder;
    invoke-direct {{v5}}, Landroid/content/pm/SigningDetails$Builder;-><init>()V

    invoke-virtual {{v5, v4}}, Landroid/content/pm/SigningDetails$Builder;->setSignatures([Landroid/content/pm/Signature;)Landroid/content/pm/SigningDetails$Builder;
    move-result-object v5

    const/4 v6, 0x3
    invoke-virtual {{v5, v6}}, Landroid/content/pm/SigningDetails$Builder;->setSignatureSchemeVersion(I)Landroid/content/pm/SigningDetails$Builder;
    move-result-object v5

    invoke-virtual {{v5}}, Landroid/content/pm/SigningDetails$Builder;->build()Landroid/content/pm/SigningDetails;
    move-result-object v5

    new-instance v6, Landroid/content/pm/SigningInfo;
    invoke-direct {{v6, v5}}, Landroid/content/pm/SigningInfo;-><init>(Landroid/content/pm/SigningDetails;)V

    iput-object v6, p0, Landroid/content/pm/PackageInfo;->signingInfo:Landroid/content/pm/SigningInfo;

    :cond_log
    const-string v5, "LaForgeSpoof"
    invoke-static {{v5, v0}}, Landroid/util/Slog;->i(Ljava/lang/String;Ljava/lang/String;)I
    :try_end_spoof
    .catch Ljava/lang/Throwable; {{:try_start_spoof .. :try_end_spoof}} :catch_spoof

    goto :cond_done

    :catch_spoof
    move-exception v0

    :cond_done
    return-void
.end method
"""
    smali_path.write_text(text + "\n" + helper_method, encoding="utf-8")
    return True


def patch_permission_manager_smali(smali_path: Path) -> bool:
    """Grant android.permission.FAKE_PACKAGE_SIGNATURE in PermissionManagerServiceImpl.smali."""
    text = smali_path.read_text(encoding="utf-8")
    if "android.permission.FAKE_PACKAGE_SIGNATURE" in text:
        return False

    spoof_hook = """    const-string v0, "android.permission.FAKE_PACKAGE_SIGNATURE"
    invoke-virtual {v0, p2}, Ljava/lang/String;->equals(Ljava/lang/Object;)Z
    move-result v0
    if-eqz v0, :cond_laforge_not_fake_sig
    const/4 v0, 0x0
    return v0
    :cond_laforge_not_fake_sig
"""
    text = text.replace(
        ".method public checkPermission(Ljava/lang/String;Ljava/lang/String;I)I\n    .locals 7",
        ".method public checkPermission(Ljava/lang/String;Ljava/lang/String;I)I\n    .locals 7\n" + spoof_hook,
        1,
    )
    text = text.replace(
        ".method public checkUidPermission(ILjava/lang/String;I)I\n    .locals 6",
        ".method public checkUidPermission(ILjava/lang/String;I)I\n    .locals 6\n" + spoof_hook,
        1,
    )
    smali_path.write_text(text, encoding="utf-8")
    return True


def build_patched_services_jar(
    orig_jar: Path,
    patched_jar: Path,
    baksmali_jar: Path,
    smali_jar: Path,
    java_exe: str = "java",
    work_dir: Path | None = None,
) -> Path:
    """Disassemble classes2.dex from services.jar, apply signature spoofing patches, and reassemble."""
    if work_dir is None:
        work_dir = orig_jar.parent / "work_services_patch"
    if work_dir.exists():
        shutil.rmtree(work_dir)
    work_dir.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(orig_jar, "r") as zf:
        zf.extract("classes2.dex", work_dir)

    classes2_dex = work_dir / "classes2.dex"
    smali_dir = work_dir / "smali_classes2"

    subprocess.run(
        [java_exe, "-Xmx2g", "-jar", str(baksmali_jar), "d", str(classes2_dex), "-o", str(smali_dir), "--api", "34"],
        check=True,
    )

    pkg_info_utils = smali_dir / "com/android/server/pm/parsing/PackageInfoUtils.smali"
    perm_mgr = smali_dir / "com/android/server/pm/permission/PermissionManagerServiceImpl.smali"

    patch_package_info_utils_smali(pkg_info_utils)
    patch_permission_manager_smali(perm_mgr)

    patched_dex = work_dir / "classes2_patched.dex"
    subprocess.run(
        [java_exe, "-Xmx2g", "-jar", str(smali_jar), "a", str(smali_dir), "-o", str(patched_dex), "--api", "34"],
        check=True,
    )

    shutil.copy2(orig_jar, patched_jar)
    shutil.copy2(patched_dex, classes2_dex)
    subprocess.run(["jar", "-uf", str(patched_jar), "-C", str(work_dir), "classes2.dex"], check=True)
    return patched_jar


def apply_signature_spoofing_to_avd(
    adb_path: str,
    patched_services_jar: Path = Path("runtime/gms/services_patched.jar"),
    timeout_sec: int = 90,
) -> bool:
    """
    Push `services_patched.jar` to `/data/system/laforge_services.jar`, bind-mount it over
    `/system/framework/services.jar` (and mask stale precompiled `.odex`/`.vdex` so ART loads
    our patched `classes2.dex`), and soft-restart Android userspace if not already active.
    Must run BEFORE microG is injected via ADB.
    """
    if not patched_services_jar.is_file():
        raise FileNotFoundError(f"Patched services.jar not found: {patched_services_jar}")

    # Check if already mounted and active in current runtime session
    active = subprocess.run(
        [adb_path, "shell", "getprop", "debug.laforge.sigspoof_active"],
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    if active == "1":
        print("[LaForge Spoof] Signature spoofing already active in system_server.")
        return True

    print("[LaForge Spoof] Elevating ADB to root and pushing patched services.jar...")
    subprocess.run([adb_path, "root"], check=False, capture_output=True)
    subprocess.run([adb_path, "wait-for-device"], check=True)

    remote_jar = "/data/system/laforge_services.jar"
    subprocess.run([adb_path, "push", str(patched_services_jar), remote_jar], check=True)

    mount_script = (
        f"chmod 644 {remote_jar}; "
        f"chown root:root {remote_jar}; "
        f"umount /system/framework/services.jar 2>/dev/null; "
        f"mount -o bind {remote_jar} /system/framework/services.jar; "
        "dex2oat --dex-file=/system/framework/services.jar "
        "--oat-file=/data/system/laforge_services.odex "
        "--instruction-set=x86_64 --compiler-filter=verify 2>/dev/null || true; "
        "if [ -f /data/system/laforge_services.odex ] && [ -f /data/system/laforge_services.vdex ]; then "
        "  umount /system/framework/oat/x86_64/services.odex 2>/dev/null; "
        "  umount /system/framework/oat/x86_64/services.vdex 2>/dev/null; "
        "  mount -o bind /data/system/laforge_services.odex /system/framework/oat/x86_64/services.odex; "
        "  mount -o bind /data/system/laforge_services.vdex /system/framework/oat/x86_64/services.vdex; "
        "fi; "
        "setprop sys.boot_completed 0; "
        "stop; start"
    )
    subprocess.run([adb_path, "shell", mount_script], check=True)

    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        boot = subprocess.run(
            [adb_path, "shell", "getprop", "sys.boot_completed"],
            capture_output=True,
            text=True,
            check=False,
        ).stdout.strip()
        if boot == "1":
            subprocess.run(
                [adb_path, "shell", "setprop", "debug.laforge.sigspoof_active", "1"],
                check=False,
            )
            print("[LaForge Spoof] system_server restarted with patched services.jar (Signature Spoofing ACTIVE).")
            return True
        time.sleep(1.5)

    raise TimeoutError("Timed out waiting for system_server soft-restart after applying signature spoofing patch.")
