#!/usr/bin/env python3
"""将 engine/openshiina/patches/ 中的补丁队列应用到锁定 OpenShiina 上游工作区。"""

from __future__ import annotations

import argparse
import sys
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
    parser = argparse.ArgumentParser(description="将 OpenShiina 补丁队列应用到外部上游工作区")
    parser.add_argument("--source", required=True, type=Path, help="外部 OpenShiina git 工作区")
    parser.add_argument("--lock", type=Path, default=LOCK_PATH, help="source-lock.json 路径")
    parser.add_argument("--patches-dir", type=Path, default=PATCHES_DIR, help="patches 目录")
    parser.add_argument("--series-file", type=Path, default=SERIES_PATH, help="series 文件路径")
    parser.add_argument("--no-reset", action="store_true", help="跳过自动重置到锁定提交")
    parser.add_argument("--check", action="store_true", help="仅预检补丁能否干净应用，不改写分支")
    return parser.parse_args(argv)


def apply_patches(
    source_dir: Path,
    *,
    lock_path: Path = LOCK_PATH,
    patches_dir: Path = PATCHES_DIR,
    series_file: Path = SERIES_PATH,
    reset: bool = True,
    check_only: bool = False,
) -> int:
    """校验并应用全部补丁，返回已应用的补丁数。"""
    if not (source_dir / ".git").exists():
        raise FileNotFoundError(f"上游目录不是 git 仓库：{source_dir}")

    lock = load_lock(lock_path)
    base_commit = str(lock["upstream"]["commit"])
    patches = validate_patch_series_sync(lock, patches_dir=patches_dir, series_path=series_file)

    if check_only:
        head_sha = run_git(source_dir, ["rev-parse", "HEAD"]).stdout.strip()
        if head_sha != base_commit:
            raise RuntimeError(
                f"--check 模式要求工作区处于基线提交 {base_commit}，当前为 {head_sha}"
            )
        for patch_path in patches:
            run_git(source_dir, ["apply", "--check", str(patch_path)])
        return len(patches)

    if reset:
        # 清理未完成 am 会话并强制回到锁定基线提交
        run_git(source_dir, ["am", "--abort"], check=False)
        run_git(source_dir, ["checkout", "--detach", base_commit])
        run_git(source_dir, ["reset", "--hard", base_commit])
        run_git(source_dir, ["clean", "-fd"])

    for patch_path in patches:
        run_git(source_dir, ["am", "--3way", "--keep-cr", str(patch_path)])

    return len(patches)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        count = apply_patches(
            args.source.resolve(),
            lock_path=args.lock.resolve(),
            patches_dir=args.patches_dir.resolve(),
            series_file=args.series_file.resolve(),
            reset=not args.no_reset,
            check_only=args.check,
        )
    except Exception as exc:  # noqa: BLE001
        print(f"[apply-patches] FAILED: {exc}", file=sys.stderr)
        return 1

    mode = "CHECK OK" if args.check else "APPLIED"
    print(f"[apply-patches] {mode}: {count} patch(es).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
