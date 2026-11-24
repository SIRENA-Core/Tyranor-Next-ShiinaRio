#!/usr/bin/env python3
"""可复现构建并校验 OpenShiina 外置宿主 APK，并将所有中间产物输出到仓库外部目录。"""

from __future__ import annotations

import argparse
import importlib.util
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS_DIR))
from openshiina_common import (  # noqa: E402
    GAMES_CATALOG_PATH,
    HOST_MANIFEST_PATH,
    LOCK_PATH,
    REPO_ROOT,
    load_lock,
    run_git,
)


def _load_tool(filename: str, module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, TOOLS_DIR / filename)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


apply_patches_mod = _load_tool("apply-patches.py", "apply_patches_build")
verify_upstream_mod = _load_tool("verify-upstream.py", "verify_upstream_build")
export_catalog_mod = _load_tool("export-catalog.py", "export_catalog_build")
check_manifest_mod = _load_tool("check-host-manifest.py", "check_manifest_build")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="构建并校验 Tyranor-Next-ShiinaRio 宿主 APK")
    parser.add_argument("--source", required=True, type=Path, help="外部 OpenShiina 上游工作区路径")
    parser.add_argument("--build-root", required=True, type=Path, help="外部构建与产物输出目录（位于仓库外）")
    parser.add_argument("--output-apk", type=Path, default=None, help="可选：复制最终已签名 APK 到指定路径")
    parser.add_argument("--configuration", default="Release", choices=("Debug", "Release"), help="MSBuild 配置（默认 Release）")
    parser.add_argument("--dev", action="store_true", help="使用临时调试密钥在 --build-root 内自动签名（仅开发测试）")
    parser.add_argument("--keystore", type=Path, default=None, help="Release 签名 keystore 路径")
    parser.add_argument("--store-pass", default=None, help="keystore 密码（或通过 ANDROID_KEYSTORE_PASSWORD 传入）")
    parser.add_argument("--key-alias", default=None, help="签名 alias（或通过 ANDROID_KEY_ALIAS 传入）")
    parser.add_argument("--key-pass", default=None, help="key 密码（或通过 ANDROID_KEY_PASSWORD 传入）")
    parser.add_argument("--dotnet", type=Path, default=None, help="显式指定 dotnet 可执行文件路径")
    parser.add_argument("--android-sdk", type=Path, default=None, help="显式指定 AndroidSdkDirectory")
    parser.add_argument("--java-home", type=Path, default=None, help="显式指定 JavaSdkDirectory")
    parser.add_argument("--rids", default=None, help="可选：覆写 RuntimeIdentifiers（例如 android-arm64）")
    parser.add_argument("--skip-apply-patches", action="store_true", help="假设外部工作区已干净打好补丁，跳过重置")
    return parser.parse_args(argv)


def find_dotnet(explicit: Path | None) -> Path:
    if explicit is not None:
        return explicit
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        user_dotnet = Path(local_app_data) / "Microsoft" / "dotnet" / "dotnet.exe"
        if user_dotnet.is_file():
            return user_dotnet
    which_dotnet = shutil.which("dotnet")
    if which_dotnet:
        return Path(which_dotnet)
    raise FileNotFoundError("找不到 dotnet 可执行文件，请安装 .NET 10 SDK 与 android 工作负载")


def find_android_sdk(explicit: Path | None) -> Path:
    if explicit is not None:
        return explicit
    for env_var in ("ANDROID_HOME", "ANDROID_SDK_ROOT"):
        val = os.environ.get(env_var)
        if val and Path(val).is_dir():
            return Path(val)
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        candidate = Path(local_app_data) / "Android" / "Sdk"
        if candidate.is_dir():
            return candidate
    home_candidate = Path.home() / "Android" / "Sdk"
    if home_candidate.is_dir():
        return home_candidate
    raise FileNotFoundError("找不到 Android SDK，请设置 ANDROID_HOME 或传递 --android-sdk")


def find_java_home(explicit: Path | None) -> Path:
    if explicit is not None:
        return explicit
    if os.environ.get("JAVA_HOME") and Path(os.environ["JAVA_HOME"]).is_dir():
        return Path(os.environ["JAVA_HOME"])
    javac = shutil.which("javac")
    if javac:
        return Path(javac).resolve().parent.parent
    adoptium_root = Path("C:/Program Files/Eclipse Adoptium")
    if adoptium_root.is_dir():
        jdks = sorted(adoptium_root.glob("jdk-17*"), reverse=True)
        if jdks:
            return jdks[0]
    raise FileNotFoundError("找不到 JDK 17，请设置 JAVA_HOME 或传递 --java-home")


def find_build_tool(android_sdk: Path, name: str) -> Path:
    build_tools_root = android_sdk / "build-tools"
    if not build_tools_root.is_dir():
        raise FileNotFoundError(f"找不到 Android build-tools 目录：{build_tools_root}")
    candidates: list[Path] = []
    exts = (".exe", ".bat", "") if os.name == "nt" else ("",)
    for version_dir in sorted(build_tools_root.iterdir(), reverse=True):
        if not version_dir.is_dir():
            continue
        for ext in exts:
            tool = version_dir / f"{name}{ext}"
            if tool.is_file():
                candidates.append(tool)
                break
    if not candidates:
        raise FileNotFoundError(f"在 {build_tools_root} 中找不到 {name}")
    return candidates[0]


def ensure_dev_keystore(build_root: Path, java_home: Path) -> tuple[Path, str, str, str]:
    keystore = build_root / "dev-debug.keystore"
    store_pass = "tyranor-dev-debug"
    key_alias = "tyranor-dev"
    key_pass = "tyranor-dev-debug"
    if keystore.is_file():
        return keystore, store_pass, key_alias, key_pass

    keytool_name = "keytool.exe" if os.name == "nt" else "keytool"
    keytool = java_home / "bin" / keytool_name
    if not keytool.is_file():
        which_kt = shutil.which("keytool")
        if not which_kt:
            raise FileNotFoundError(f"找不到 keytool：{keytool}")
        keytool = Path(which_kt)

    keystore.parent.mkdir(parents=True, exist_ok=True)
    proc = subprocess.run(
        [
            str(keytool),
            "-genkeypair",
            "-v",
            "-keystore",
            str(keystore),
            "-storepass",
            store_pass,
            "-alias",
            key_alias,
            "-keypass",
            key_pass,
            "-keyalg",
            "RSA",
            "-keysize",
            "2048",
            "-validity",
            "3650",
            "-dname",
            "CN=Tyranor Dev Debug, O=Tyranor-Next, C=CN",
        ],
        check=False,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"生成开发调试 keystore 失败：{proc.stderr or proc.stdout}")
    return keystore, store_pass, key_alias, key_pass


def verify_apk_contents(apk_path: Path, catalog_path: Path, lock: dict) -> None:
    """校验编译出的 APK 包含 byte-equal games.json 与上游 Formats.Json。"""
    expected_catalog = catalog_path.read_bytes()
    asset_rel = lock.get("catalog", {}).get("assetPath", "assets/tyn/games.json")
    with zipfile.ZipFile(apk_path, "r") as zf:
        names = set(zf.namelist())
        if asset_rel not in names:
            raise RuntimeError(f"APK 缺少内嵌目录资源：{asset_rel}")
        actual_catalog = zf.read(asset_rel)
        if actual_catalog != expected_catalog:
            raise RuntimeError(
                f"APK 内 {asset_rel} 与仓库 engine/openshiina/games.json 字节不一致"
            )
        if "assets/Formats.Json" not in names:
            raise RuntimeError("APK 缺少上游格式数据库 assets/Formats.Json")


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    source_dir = args.source.resolve()
    build_root = args.build_root.resolve()

    if REPO_ROOT in build_root.parents or build_root == REPO_ROOT:
        print(f"[build-apk] FAILED: --build-root 必须位于仓库外部：{build_root}", file=sys.stderr)
        return 1
    if source_dir in build_root.parents or build_root == source_dir:
        print(f"[build-apk] FAILED: --build-root 必须位于上游外部：{build_root}", file=sys.stderr)
        return 1

    try:
        lock = load_lock(LOCK_PATH)
        if not args.skip_apply_patches:
            apply_patches_mod.apply_patches(source_dir)
        verify_upstream_mod.verify_upstream_checkout(source_dir, patched=True)
        export_catalog_mod.export_catalog(source_dir, check_only=True)
        check_manifest_mod.check_manifest_xml(HOST_MANIFEST_PATH, LOCK_PATH)

        dotnet = find_dotnet(args.dotnet)
        android_sdk = find_android_sdk(args.android_sdk)
        java_home = find_java_home(args.java_home)
        aapt2 = find_build_tool(android_sdk, "aapt2")
        apksigner = find_build_tool(android_sdk, "apksigner")

        keystore = args.keystore or (
            Path(os.environ["ANDROID_KEYSTORE_PATH"]) if os.environ.get("ANDROID_KEYSTORE_PATH") else None
        )
        store_pass = args.store_pass or os.environ.get("ANDROID_KEYSTORE_PASSWORD")
        key_alias = args.key_alias or os.environ.get("ANDROID_KEY_ALIAS")
        key_pass = args.key_pass or os.environ.get("ANDROID_KEY_PASSWORD")

        if args.dev:
            keystore, store_pass, key_alias, key_pass = ensure_dev_keystore(build_root, java_home)
        elif not all([keystore, store_pass, key_alias, key_pass]):
            raise ValueError("非 --dev 模式必须提供签名凭据（--keystore / --store-pass / --key-alias / --key-pass）")

        project_path = (REPO_ROOT / lock["host"]["project"]).resolve()
        artifacts_dir = build_root / "artifacts"
        publish_dir = build_root / "publish"
        artifacts_dir.mkdir(parents=True, exist_ok=True)
        publish_dir.mkdir(parents=True, exist_ok=True)

        env = os.environ.copy()
        env["ANDROID_HOME"] = str(android_sdk)
        env["ANDROID_SDK_ROOT"] = str(android_sdk)
        env["JAVA_HOME"] = str(java_home)
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"

        cmd = [
            str(dotnet),
            "publish",
            str(project_path),
            "-c",
            args.configuration,
            "-f",
            "net10.0-android",
            "-o",
            str(publish_dir),
            "-p:UseArtifactsOutput=true",
            "-p:UseCurrentRuntimeIdentifier=false",
            "-p:UseDefaultPublishRuntimeIdentifier=false",
            f"-p:ArtifactsPath={artifacts_dir}",
            f"-p:OpenShiinaSource={source_dir}",
            f"-p:AndroidSdkDirectory={android_sdk}",
            f"-p:JavaSdkDirectory={java_home}",
            "-p:AndroidPackageFormat=apk",
            "-p:AndroidKeyStore=true",
            f"-p:AndroidSigningKeyStore={keystore}",
            f"-p:AndroidSigningStorePass={store_pass}",
            f"-p:AndroidSigningKeyAlias={key_alias}",
            f"-p:AndroidSigningKeyPass={key_pass}",
        ]
        if args.rids:
            cmd.append(f"-p:RuntimeIdentifiers={args.rids}")

        proc = subprocess.run(cmd, env=env, check=False)
        if proc.returncode != 0:
            raise RuntimeError(f"dotnet publish 失败，退出码：{proc.returncode}")

        # 确认上游工作区未被构建过程修改
        upstream_status = run_git(source_dir, ["status", "--porcelain"]).stdout.strip()
        if upstream_status:
            raise RuntimeError(f"构建污染了上游工作区：\n{upstream_status}")

        signed_apks = sorted(publish_dir.glob("*-Signed.apk"))
        if not signed_apks:
            signed_apks = sorted(publish_dir.glob("*.apk"))
        if not signed_apks:
            raise FileNotFoundError(f"在 {publish_dir} 未找到输出的 .apk 文件")
        apk_path = signed_apks[0]

        verify_sig = subprocess.run(
            [str(apksigner), "verify", "--verbose", str(apk_path)],
            check=False,
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
        )
        if verify_sig.returncode != 0:
            raise RuntimeError(f"apksigner verify 失败：{verify_sig.stderr or verify_sig.stdout}")

        check_manifest_mod.check_apk_with_aapt2(apk_path, aapt2, LOCK_PATH)
        verify_apk_contents(apk_path, GAMES_CATALOG_PATH, lock)

        if args.output_apk is not None:
            out_path = args.output_apk.resolve()
            out_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(apk_path, out_path)
            apk_path = out_path

        print(f"[build-apk] OK: {apk_path}")
        return 0
    except Exception as exc:  # noqa: BLE001
        print(f"[build-apk] FAILED: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
