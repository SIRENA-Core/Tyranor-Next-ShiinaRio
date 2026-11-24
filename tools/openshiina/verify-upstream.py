#!/usr/bin/env python3
"""校验 OpenShiina 上游工作区提交、补丁状态与目录锁一致性。"""

from __future__ import annotations

import argparse
import sys
from email.header import decode_header
from email.parser import HeaderParser
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from openshiina_common import (  # noqa: E402
    LOCK_PATH,
    PATCHES_DIR,
    SERIES_PATH,
    load_lock,
    run_git,
    validate_patch_series_sync,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="校验 OpenShiina 上游克隆与补丁状态")
    parser.add_argument(
        "--source",
        required=True,
        type=Path,
        help="外部 OpenShiina 上游 git 工作区路径",
    )
    parser.add_argument(
        "--lock",
        type=Path,
        default=LOCK_PATH,
        help="source-lock.json 路径（默认指向仓库内锁定文件）",
    )
    parser.add_argument(
        "--patches-dir",
        type=Path,
        default=PATCHES_DIR,
        help="patches 目录路径",
    )
    parser.add_argument(
        "--series-file",
        type=Path,
        default=SERIES_PATH,
        help="patches/series 文件路径",
    )
    parser.add_argument(
        "--patched",
        action="store_true",
        help="校验已应用 patches/series 的工作区（而非纯净锁定基线）",
    )
    return parser.parse_args(argv)


def extract_patch_subject(patch_path: Path) -> str:
    """从 format-patch 文件中提取并解码规范化 Subject 标题。"""
    text = patch_path.read_text(encoding="utf-8", errors="replace")
    headers = HeaderParser().parsestr(text, headersonly=True)
    raw_subject = headers.get("Subject", "")
    if not raw_subject:
        return ""
    parts: list[str] = []
    for chunk, charset in decode_header(raw_subject):
        if isinstance(chunk, bytes):
            parts.append(chunk.decode(charset or "utf-8", errors="replace"))
        else:
            parts.append(chunk)
    subject = "".join(parts).strip()
    if subject.startswith("[PATCH") and "]" in subject:
        return subject.split("]", 1)[1].strip()
    return subject


def verify_upstream_checkout(
    source_dir: Path,
    *,
    lock_path: Path = LOCK_PATH,
    patches_dir: Path = PATCHES_DIR,
    series_file: Path = SERIES_PATH,
    patched: bool = False,
) -> dict[str, object]:
    """执行上游工作区一致性检查并返回摘要信息。"""
    if not (source_dir / ".git").exists():
        raise FileNotFoundError(f"上游目录不是有效的 git 仓库：{source_dir}")

    lock = load_lock(lock_path)
    expected_commit = str(lock["upstream"]["commit"])
    patches = validate_patch_series_sync(lock, patches_dir=patches_dir, series_path=series_file)

    status_out = run_git(source_dir, ["status", "--porcelain"]).stdout.strip()
    if status_out:
        raise RuntimeError(f"上游工作区不干净，请清理未提交改动：\n{status_out}")

    head_sha = run_git(source_dir, ["rev-parse", "HEAD"]).stdout.strip()
    if not patched or not patches:
        if head_sha != expected_commit:
            raise RuntimeError(
                f"上游 HEAD 与 source-lock.json 不一致：expected={expected_commit}, actual={head_sha}"
            )
        return {
            "mode": "patched" if patched else "clean",
            "head": head_sha,
            "baseCommit": expected_commit,
            "patchCount": len(patches),
        }

    # 已打补丁模式：HEAD 必须从 expected_commit 严格前进 len(patches) 个提交，且主题逐一对齐
    ahead_out = run_git(
        source_dir, ["rev-list", "--reverse", "--format=%s", f"{expected_commit}..HEAD"]
    ).stdout
    subjects = [line for line in ahead_out.splitlines() if line and not line.startswith("commit ")]
    if len(subjects) != len(patches):
        raise RuntimeError(
            f"已应用补丁数量与 series 不符：expected={len(patches)}, actual={len(subjects)}"
        )

    merge_base = run_git(source_dir, ["merge-base", expected_commit, "HEAD"]).stdout.strip()
    if merge_base != expected_commit:
        raise RuntimeError(
            f"HEAD 不是基于锁定提交 {expected_commit} 线性演进的（merge-base={merge_base}）"
        )

    for index, (patch_path, commit_subject) in enumerate(zip(patches, subjects), start=1):
        expected_subject = extract_patch_subject(patch_path)
        if expected_subject and commit_subject.strip() != expected_subject:
            raise RuntimeError(
                f"第 {index} 个补丁主题不匹配 ({patch_path.name})："
                f"expected={expected_subject!r}, actual={commit_subject!r}"
            )

    return {
        "mode": "patched",
        "head": head_sha,
        "baseCommit": expected_commit,
        "patchCount": len(patches),
    }


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        result = verify_upstream_checkout(
            args.source.resolve(),
            lock_path=args.lock.resolve(),
            patches_dir=args.patches_dir.resolve(),
            series_file=args.series_file.resolve(),
            patched=args.patched,
        )
    except Exception as exc:  # noqa: BLE001
        print(f"[verify-upstream] FAILED: {exc}", file=sys.stderr)
        return 1

    print(
        f"[verify-upstream] OK ({result['mode']}): "
        f"base={result['baseCommit'][:12]}, head={str(result['head'])[:12]}, patches={result['patchCount']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
