#!/usr/bin/env python3
"""将 OpenShiina 上游同步到目标提交，验证/重放补丁队列、刷新 games.json 并更新 source-lock.json。"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any

TOOLS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS_DIR))
from openshiina_common import (  # noqa: E402
    GAMES_CATALOG_PATH,
    LOCK_PATH,
    PATCHES_DIR,
    SERIES_PATH,
    load_lock,
    run_git,
    save_lock,
    validate_patch_series_sync,
)


def _load_tool(filename: str, module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, TOOLS_DIR / filename)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


apply_patches_mod = _load_tool("apply-patches.py", "apply_patches_mod")
export_catalog_mod = _load_tool("export-catalog.py", "export_catalog_mod")


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="同步 OpenShiina 上游提交与游戏目录清单")
    parser.add_argument("--source", required=True, type=Path, help="外部 OpenShiina git 工作区")
    parser.add_argument("--lock", type=Path, default=LOCK_PATH, help="source-lock.json 路径")
    parser.add_argument("--patches-dir", type=Path, default=PATCHES_DIR, help="patches 目录")
    parser.add_argument("--series-file", type=Path, default=SERIES_PATH, help="series 文件路径")
    parser.add_argument("--catalog", type=Path, default=GAMES_CATALOG_PATH, help="games.json 输出路径")
    parser.add_argument("--target-commit", default=None, help="指定同步的目标提交（默认拉取远端跟踪分支）")
    parser.add_argument("--branch", default=None, help="指定上游分支名（默认读取 source-lock.json）")
    parser.add_argument("--no-fetch", action="store_true", help="离线模式：跳过 git fetch，仅从本地引用解析")
    parser.add_argument("--release-tag", default=None, help="可选：发版标签（如 v0.1.0），自动同步 host.versionName 与递增 versionCode")
    parser.add_argument("--version-code", type=int, default=None, help="可选：显式指定 host.versionCode")
    parser.add_argument("--dry-run", action="store_true", help="试运行：验证合并与目录生成但不写入仓库文件")
    parser.add_argument("--json", action="store_true", help="以 JSON 格式输出同步摘要")
    parser.add_argument("--github-output", type=Path, default=None, help="可选：写入 GITHUB_OUTPUT 格式变量")
    return parser.parse_args(argv)


def clone_if_missing(source_dir: Path, repo_url: str) -> None:
    """若工作区不存在则浅克隆或完整克隆上游仓库。"""
    if (source_dir / ".git").exists():
        return
    source_dir.parent.mkdir(parents=True, exist_ok=True)
    proc = subprocess.run(
        ["git", "-c", "core.autocrlf=false", "clone", repo_url, str(source_dir)],
        check=False,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
    )
    if proc.returncode != 0:
        raise RuntimeError(f"克隆上游仓库失败：{proc.stderr or proc.stdout}")


def _parse_release_version_name(release_tag: str) -> str:
    raw = release_tag.strip()
    if not raw or any(ch.isspace() for ch in raw) or "/" in raw:
        raise ValueError(f"非法的 release_tag：{release_tag!r}")
    version_name = raw[1:] if raw.startswith("v") else raw
    if not version_name:
        raise ValueError(f"非法的 release_tag：{release_tag!r}")
    return version_name


def resolve_tag_for_commit(source_dir: Path, commit: str) -> str:
    """查找命中的精确 tag，否则退回最近的可达 tag；无 tag 时返回空串。"""
    exact = run_git(source_dir, ["describe", "--tags", "--exact-match", commit], check=False)
    if exact.returncode == 0 and exact.stdout.strip():
        return exact.stdout.strip()
    nearest = run_git(source_dir, ["describe", "--tags", "--abbrev=0", commit], check=False)
    if nearest.returncode == 0 and nearest.stdout.strip():
        return nearest.stdout.strip()
    return ""


def sync_upstream(
    source_dir: Path,
    *,
    lock_path: Path = LOCK_PATH,
    patches_dir: Path = PATCHES_DIR,
    series_file: Path = SERIES_PATH,
    catalog_path: Path = GAMES_CATALOG_PATH,
    target_commit: str | None = None,
    branch: str | None = None,
    release_tag: str | None = None,
    version_code: int | None = None,
    fetch: bool = True,
    dry_run: bool = False,
) -> dict[str, Any]:
    """同步外部上游到新提交并更新锁定文件与 games.json。"""
    lock = load_lock(lock_path)
    upstream_cfg = lock["upstream"]
    old_commit = str(upstream_cfg["commit"])
    old_tag = str(upstream_cfg.get("tag", ""))
    target_branch = branch or str(upstream_cfg.get("branch", "master"))

    clone_if_missing(source_dir, str(upstream_cfg["repo"]))
    run_git(source_dir, ["config", "core.autocrlf", "false"])

    if fetch:
        run_git(source_dir, ["fetch", "--tags", "origin"])

    if target_commit:
        resolved_ref = target_commit
    elif fetch:
        resolved_ref = f"origin/{target_branch}"
    else:
        resolved_ref = target_branch

    new_commit = run_git(source_dir, ["rev-parse", f"{resolved_ref}^{{commit}}"]).stdout.strip()
    new_tag = resolve_tag_for_commit(source_dir, new_commit)

    # 先清理并切换到新提交，验证 patches/series 能否干净重放
    run_git(source_dir, ["am", "--abort"], check=False)
    run_git(source_dir, ["checkout", "--detach", new_commit])
    run_git(source_dir, ["reset", "--hard", new_commit])
    run_git(source_dir, ["clean", "-fd"])

    patches = validate_patch_series_sync(lock, patches_dir=patches_dir, series_path=series_file)
    for patch_path in patches:
        run_git(source_dir, ["am", "--3way", "--keep-cr", str(patch_path)])

    updated_lock = json.loads(json.dumps(lock))
    updated_lock["upstream"]["branch"] = target_branch
    updated_lock["upstream"]["commit"] = new_commit
    updated_lock["upstream"]["tag"] = new_tag

    host_cfg = updated_lock.get("host")
    if (release_tag is not None or version_code is not None) and isinstance(host_cfg, dict):
        old_version_name = str(host_cfg.get("versionName", ""))
        old_version_code = int(host_cfg.get("versionCode", 1))
        target_version_name = old_version_name
        if release_tag is not None:
            target_version_name = _parse_release_version_name(release_tag)
            host_cfg["versionName"] = target_version_name
        if version_code is not None:
            host_cfg["versionCode"] = int(version_code)
        elif release_tag is not None and target_version_name != old_version_name:
            host_cfg["versionCode"] = old_version_code + 1
    version_changed = updated_lock.get("host") != lock.get("host")

    rel_formats = updated_lock.get("catalog", {}).get(
        "sourcePath", "src/OpenShiina.Core/Data/Formats.Json"
    )
    payload = export_catalog_mod.build_catalog_payload(source_dir / rel_formats, updated_lock)
    new_catalog_text = export_catalog_mod.serialize_catalog(payload)
    old_catalog_text = (
        catalog_path.read_text(encoding="utf-8") if catalog_path.is_file() else ""
    )
    catalog_changed = new_catalog_text != old_catalog_text
    commit_changed = old_commit != new_commit or old_tag != new_tag

    if not dry_run:
        if commit_changed or version_changed or not lock_path.is_file():
            save_lock(updated_lock, lock_path)
        if catalog_changed or not catalog_path.is_file():
            export_catalog_mod.export_catalog(
                source_dir, lock_path=lock_path, output_path=catalog_path, check_only=False
            )

    resolved_host = updated_lock.get("host") if isinstance(updated_lock.get("host"), dict) else {}
    return {
        "changed": commit_changed or catalog_changed or version_changed,
        "commitChanged": commit_changed,
        "catalogChanged": catalog_changed,
        "versionChanged": version_changed,
        "oldCommit": old_commit,
        "newCommit": new_commit,
        "oldTag": old_tag,
        "newTag": new_tag,
        "versionCode": resolved_host.get("versionCode", ""),
        "versionName": resolved_host.get("versionName", ""),
        "patchCount": len(patches),
        "schemeCount": len(payload["schemes"]),
        "mappedFilesCount": len(payload["gameMap"]),
        "formatsVersion": payload["formatsVersion"],
        "dryRun": dry_run,
    }


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        summary = sync_upstream(
            args.source.resolve(),
            lock_path=args.lock.resolve(),
            patches_dir=args.patches_dir.resolve(),
            series_file=args.series_file.resolve(),
            catalog_path=args.catalog.resolve(),
            target_commit=args.target_commit,
            branch=args.branch,
            release_tag=args.release_tag,
            version_code=args.version_code,
            fetch=not args.no_fetch,
            dry_run=args.dry_run,
        )
    except Exception as exc:  # noqa: BLE001
        print(f"[sync-upstream] FAILED: {exc}", file=sys.stderr)
        return 1

    gh_out = args.github_output or (
        Path(os.environ["GITHUB_OUTPUT"]) if os.environ.get("GITHUB_OUTPUT") else None
    )
    if gh_out is not None:
        lines = [
            f"changed={'true' if summary['changed'] else 'false'}",
            f"version_changed={'true' if summary['versionChanged'] else 'false'}",
            f"old_commit={summary['oldCommit']}",
            f"new_commit={summary['newCommit']}",
            f"old_tag={summary['oldTag']}",
            f"new_tag={summary['newTag']}",
            f"version_code={summary['versionCode']}",
            f"version_name={summary['versionName']}",
            f"patch_count={summary['patchCount']}",
            f"scheme_count={summary['schemeCount']}",
            f"mapped_files_count={summary['mappedFilesCount']}",
            f"formats_version={summary['formatsVersion']}",
        ]
        gh_out.parent.mkdir(parents=True, exist_ok=True)
        with gh_out.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write("\n".join(lines) + "\n")

    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
    else:
        status = "UPDATED" if summary["changed"] else "UP-TO-DATE"
        print(
            f"[sync-upstream] {status}: "
            f"{summary['oldCommit'][:12]} ({summary['oldTag'] or 'no-tag'}) -> "
            f"{summary['newCommit'][:12]} ({summary['newTag'] or 'no-tag'}), "
            f"schemes={summary['schemeCount']}, formats=v{summary['formatsVersion']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
