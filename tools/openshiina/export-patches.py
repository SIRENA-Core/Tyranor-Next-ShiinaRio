#!/usr/bin/env python3
"""从外部 OpenShiina 工作区导出超出锁定基线的提交为补丁，并同步 series 与 source-lock.json。"""

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
    save_lock,
    save_series_file,
    write_lf_utf8,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="导出 OpenShiina 上游工作区补丁并更新队列清单")
    parser.add_argument("--source", required=True, type=Path, help="外部 OpenShiina git 工作区")
    parser.add_argument("--lock", type=Path, default=LOCK_PATH, help="source-lock.json 路径")
    parser.add_argument("--patches-dir", type=Path, default=PATCHES_DIR, help="patches 输出目录")
    parser.add_argument("--series-file", type=Path, default=SERIES_PATH, help="series 输出路径")
    return parser.parse_args(argv)


def export_patches(
    source_dir: Path,
    *,
    lock_path: Path = LOCK_PATH,
    patches_dir: Path = PATCHES_DIR,
    series_file: Path = SERIES_PATH,
) -> list[str]:
    """将 base_commit..HEAD 导出为有序 .patch 文件并更新 series 与 lock。"""
    if not (source_dir / ".git").exists():
        raise FileNotFoundError(f"上游目录不是 git 仓库：{source_dir}")

    lock = load_lock(lock_path)
    base_commit = str(lock["upstream"]["commit"])

    status_out = run_git(source_dir, ["status", "--porcelain"]).stdout.strip()
    if status_out:
        raise RuntimeError(f"导出补丁前请先提交或清理工作区改动：\n{status_out}")

    merge_base = run_git(source_dir, ["merge-base", base_commit, "HEAD"]).stdout.strip()
    if merge_base != base_commit:
        raise RuntimeError(
            f"HEAD 不是从锁定提交 {base_commit} 分叉的（merge-base={merge_base}）"
        )

    patches_dir.mkdir(parents=True, exist_ok=True)
    for old_patch in patches_dir.glob("*.patch"):
        old_patch.unlink()

    rev_count = int(
        run_git(source_dir, ["rev-list", "--count", f"{base_commit}..HEAD"]).stdout.strip()
    )
    exported_names: list[str] = []
    if rev_count > 0:
        out = run_git(
            source_dir,
            [
                "format-patch",
                "--no-signature",
                "--zero-commit",
                "-N",
                "--output-directory",
                str(patches_dir),
                f"{base_commit}..HEAD",
            ],
        ).stdout
        for line in out.splitlines():
            line = line.strip()
            if not line:
                continue
            patch_path = Path(line)
            if not patch_path.is_absolute():
                patch_path = patches_dir / patch_path.name
            write_lf_utf8(patch_path, patch_path.read_text(encoding="utf-8", errors="replace"))
            exported_names.append(patch_path.name)

    save_series_file(exported_names, series_file)
    lock.setdefault("patches", {})["series"] = exported_names
    save_lock(lock, lock_path)
    return exported_names


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        names = export_patches(
            args.source.resolve(),
            lock_path=args.lock.resolve(),
            patches_dir=args.patches_dir.resolve(),
            series_file=args.series_file.resolve(),
        )
    except Exception as exc:  # noqa: BLE001
        print(f"[export-patches] FAILED: {exc}", file=sys.stderr)
        return 1

    print(f"[export-patches] OK: exported {len(names)} patch(es).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
