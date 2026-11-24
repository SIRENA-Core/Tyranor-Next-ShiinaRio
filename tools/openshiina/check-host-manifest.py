#!/usr/bin/env python3
"""校验宿主 AndroidManifest.xml 及已编译 APK 不包含独立桌面启动入口且导出契约符合规范。"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from openshiina_common import HOST_MANIFEST_PATH, LOCK_PATH, load_lock  # noqa: E402

ANDROID_NS = "{http://schemas.android.com/apk/res/android}"
FORBIDDEN_CATEGORIES = (
    "android.intent.category.LAUNCHER",
    "android.intent.category.LEANBACK_LAUNCHER",
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="校验 OpenShiina 宿主清单与 APK 启动入口约束")
    parser.add_argument(
        "--manifest",
        type=Path,
        default=HOST_MANIFEST_PATH,
        help="宿主 AndroidManifest.xml 路径",
    )
    parser.add_argument(
        "--lock",
        type=Path,
        default=LOCK_PATH,
        help="source-lock.json 路径",
    )
    parser.add_argument(
        "--apk",
        type=Path,
        default=None,
        help="可选：使用 aapt2 对已构建 APK 执行运行时 badging / xmltree 校验",
    )
    parser.add_argument(
        "--aapt2",
        type=Path,
        default=None,
        help="可选：显式指定 aapt2 可执行文件路径",
    )
    return parser.parse_args(argv)


def check_manifest_xml(manifest_path: Path, lock_path: Path = LOCK_PATH) -> None:
    """静态检查 AndroidManifest.xml 是否满足无桌面图标与导出隔离要求。"""
    if not manifest_path.is_file():
        raise FileNotFoundError(f"找不到 AndroidManifest.xml：{manifest_path}")

    lock = load_lock(lock_path)
    host = lock["host"]
    expected_launch = host["launchActivity"]
    expected_runtime = host["runtimeActivity"]
    expected_action = host["launchAction"]

    tree = ET.parse(manifest_path)
    root = tree.getroot()
    app = root.find("application")
    if app is None:
        raise ValueError("AndroidManifest.xml 缺少 <application> 节点")

    app_name = app.attrib.get(f"{ANDROID_NS}name", "")
    if app_name != "com.tyranor.next.shiinario.TyranorShiinaRioApplication":
        raise ValueError(f"<application android:name> 非预期：{app_name!r}")

    for category in root.findall(".//category"):
        name = category.attrib.get(f"{ANDROID_NS}name", "")
        if name in FORBIDDEN_CATEGORIES:
            raise ValueError(f"宿主清单禁止声明桌面启动分类：{name}")

    for action in root.findall(".//action"):
        name = action.attrib.get(f"{ANDROID_NS}name", "")
        if name == "android.intent.action.MAIN":
            raise ValueError("宿主清单禁止声明 android.intent.action.MAIN 启动入口")

    activities = {
        act.attrib.get(f"{ANDROID_NS}name", ""): act
        for act in app.findall("activity")
    }

    launch_act = activities.get(expected_launch)
    if launch_act is None:
        raise ValueError(f"缺少启动入口 Activity：{expected_launch}")
    if launch_act.attrib.get(f"{ANDROID_NS}exported") != "true":
        raise ValueError(f"{expected_launch} 必须设置 android:exported=\"true\"")
    if launch_act.attrib.get(f"{ANDROID_NS}launchMode") != "singleTask":
        raise ValueError(f"{expected_launch} 必须设置 android:launchMode=\"singleTask\"")

    launch_actions = [
        act.attrib.get(f"{ANDROID_NS}name", "")
        for act in launch_act.findall("./intent-filter/action")
    ]
    if expected_action not in launch_actions:
        raise ValueError(f"{expected_launch} 缺少期望 Action：{expected_action}")

    runtime_act = activities.get(expected_runtime)
    if runtime_act is None:
        raise ValueError(f"缺少游戏运行 Activity：{expected_runtime}")
    if runtime_act.attrib.get(f"{ANDROID_NS}exported") != "false":
        raise ValueError(f"{expected_runtime} 必须设置 android:exported=\"false\"")
    if runtime_act.attrib.get(f"{ANDROID_NS}process") != ":game":
        raise ValueError(f"{expected_runtime} 必须运行在独立进程 android:process=\":game\"")


def check_apk_with_aapt2(apk_path: Path, aapt2_bin: Path, lock_path: Path = LOCK_PATH) -> None:
    """使用 aapt2 检查已编译 APK 的包名、版本码与无 launchable-activity 约束。"""
    if not apk_path.is_file():
        raise FileNotFoundError(f"找不到 APK 文件：{apk_path}")

    lock = load_lock(lock_path)
    host = lock["host"]

    badging = subprocess.run(
        [str(aapt2_bin), "dump", "badging", str(apk_path)],
        check=False,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
    )
    if badging.returncode != 0:
        raise RuntimeError(f"aapt2 dump badging 失败：{badging.stderr or badging.stdout}")

    out = badging.stdout
    pkg_match = re.search(
        r"package:\s+name='([^']+)'\s+versionCode='([^']+)'\s+versionName='([^']+)'",
        out,
    )
    if not pkg_match:
        raise RuntimeError(f"未能从 badging 输出解析 package 信息：\n{out}")

    pkg_name, version_code, version_name = pkg_match.groups()
    if pkg_name != host["packageName"]:
        raise RuntimeError(f"APK 包名不匹配：expected={host['packageName']}, actual={pkg_name}")
    if int(version_code) != int(host["versionCode"]):
        raise RuntimeError(
            f"APK versionCode 不匹配：expected={host['versionCode']}, actual={version_code}"
        )
    if version_name != host["versionName"]:
        raise RuntimeError(
            f"APK versionName 不匹配：expected={host['versionName']}, actual={version_name}"
        )

    for forbidden in ("launchable-activity:", "leanback-launchable-activity:"):
        if forbidden in out:
            raise RuntimeError(f"APK badging 包含禁止的桌面入口声明：{forbidden}")


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        check_manifest_xml(args.manifest.resolve(), args.lock.resolve())
        if args.apk is not None:
            if args.aapt2 is None:
                raise ValueError("传递 --apk 时必须同时提供 --aapt2 路径")
            check_apk_with_aapt2(args.apk.resolve(), args.aapt2.resolve(), args.lock.resolve())
    except Exception as exc:  # noqa: BLE001
        print(f"[check-host-manifest] FAILED: {exc}", file=sys.stderr)
        return 1

    print("[check-host-manifest] OK: host manifest and entry isolation verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
