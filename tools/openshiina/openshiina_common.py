#!/usr/bin/env python3
"""OpenShiina 上游锁定、补丁队列与目录工具链共享基础库。"""

from __future__ import annotations

import json
import re
import subprocess
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
ENGINE_DIR = REPO_ROOT / "engine" / "openshiina"
LOCK_PATH = ENGINE_DIR / "source-lock.json"
PATCHES_DIR = ENGINE_DIR / "patches"
SERIES_PATH = PATCHES_DIR / "series"
GAMES_CATALOG_PATH = ENGINE_DIR / "games.json"
HOST_MANIFEST_PATH = (
    ENGINE_DIR / "host-apk" / "Tyranor.OpenShiina.Host" / "Properties" / "AndroidManifest.xml"
)
HOST_CSPROJ_PATH = (
    ENGINE_DIR / "host-apk" / "Tyranor.OpenShiina.Host" / "Tyranor.OpenShiina.Host.csproj"
)

GENERIC_SCHEME_NAMES = frozenset(
    {
        "ShiinaRio v2.34 and older",
        "ShiinaRio v2.36-2.38",
    }
)

COMMIT_HEX_RE = re.compile(r"^[0-9a-f]{40}$")


def load_lock(lock_path: Path = LOCK_PATH) -> dict[str, Any]:
    """读取并校验 engine/openshiina/source-lock.json。"""
    if not lock_path.is_file():
        raise FileNotFoundError(f"找不到锁定文件：{lock_path}")
    data = json.loads(lock_path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1:
        raise ValueError(f"不支持的 source-lock schemaVersion：{data.get('schemaVersion')}")
    upstream = data.get("upstream", {})
    commit = upstream.get("commit", "")
    if not COMMIT_HEX_RE.match(commit):
        raise ValueError(f"upstream.commit 必须为 40 位小写十六进制 SHA：{commit!r}")
    return data


def save_lock(data: dict[str, Any], lock_path: Path = LOCK_PATH) -> None:
    """以确定性 UTF-8 LF JSON 格式写入 source-lock.json。"""
    text = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    write_lf_utf8(lock_path, text)


def load_series_file(series_path: Path = SERIES_PATH) -> list[str]:
    """读取 patches/series 中按顺序排列的补丁文件名（忽略空行与 # 注释）。"""
    if not series_path.is_file():
        return []
    items: list[str] = []
    for raw_line in series_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        items.append(line)
    return items


def save_series_file(entries: list[str], series_path: Path = SERIES_PATH) -> None:
    """将补丁列表写回 patches/series（LF 换行）。"""
    header = "# 按顺序应用到 OpenShiina 上游提交的补丁列表（每行一个相对本目录的 .patch 文件名；当前为空）"
    if not entries:
        write_lf_utf8(series_path, header + "\n")
        return
    body = "\n".join(entries)
    write_lf_utf8(series_path, f"# 按顺序应用到 OpenShiina 上游提交的补丁列表\n{body}\n")


def validate_patch_series_sync(
    lock: dict[str, Any],
    patches_dir: Path = PATCHES_DIR,
    series_path: Path = SERIES_PATH,
) -> list[Path]:
    """校验 source-lock.json 中的 patches.series 与 series 文件及磁盘文件完全一致。"""
    lock_series = list(lock.get("patches", {}).get("series", []))
    file_series = load_series_file(series_path)
    if lock_series != file_series:
        raise ValueError(
            "source-lock.json 中的 patches.series 与 patches/series 不一致："
            f"lock={lock_series!r}, series={file_series!r}"
        )
    resolved: list[Path] = []
    for name in lock_series:
        if "/" in name or "\\" in name or ".." in name:
            raise ValueError(f"非法补丁文件名（禁止路径穿越）：{name!r}")
        patch_path = patches_dir / name
        if not patch_path.is_file():
            raise FileNotFoundError(f"series 声明的补丁文件不存在：{patch_path}")
        resolved.append(patch_path)
    return resolved


def write_lf_utf8(path: Path, text: str) -> None:
    """以 UTF-8 无 BOM 与 LF 换行写入文本文件。"""
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write(normalized)


def run_git(
    repo_dir: Path,
    args: list[str],
    *,
    check: bool = True,
    capture: bool = True,
) -> subprocess.CompletedProcess[str]:
    """在指定 git 仓库中执行 git 命令，并返回 UTF-8 解码结果。"""
    cmd = ["git", "-C", str(repo_dir), *args]
    proc = subprocess.run(
        cmd,
        check=False,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=capture,
    )
    if check and proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip()
        raise RuntimeError(f"git 命令执行失败 ({' '.join(cmd)}): {detail}")
    return proc
