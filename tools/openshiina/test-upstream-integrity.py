#!/usr/bin/env python3
"""测试上游验证、补丁应用、补丁导出与空队列回环一致性。"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
sys_path = str(TOOLS_DIR)

import sys

sys.path.insert(0, sys_path)
from openshiina_common import save_lock, save_series_file  # noqa: E402


def _load(name: str, alias: str):
    spec = importlib.util.spec_from_file_location(alias, TOOLS_DIR / name)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


verify_mod = _load("verify-upstream.py", "test_verify_mod")
apply_mod = _load("apply-patches.py", "test_apply_mod")
export_mod = _load("export-patches.py", "test_export_mod")


def _git(repo: Path, *args: str) -> str:
    proc = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        text=True,
        encoding="utf-8",
        capture_output=True,
    )
    return proc.stdout.strip()


class UpstreamIntegrityTests(unittest.TestCase):
    def _init_repo_and_lock(self, root: Path) -> tuple[Path, Path, Path, Path, str]:
        upstream = root / "upstream"
        upstream.mkdir(parents=True)
        _git(upstream, "init", "-b", "master")
        _git(upstream, "config", "user.email", "test@example.com")
        _git(upstream, "config", "user.name", "Test User")
        (upstream / "README.md").write_text("base\n", encoding="utf-8")
        _git(upstream, "add", "README.md")
        _git(upstream, "commit", "-m", "initial commit")
        base_sha = _git(upstream, "rev-parse", "HEAD")

        patches_dir = root / "patches"
        series_file = patches_dir / "series"
        lock_path = root / "source-lock.json"
        save_series_file([], series_file)
        save_lock(
            {
                "schemaVersion": 1,
                "engine": "openshiina",
                "upstream": {
                    "repo": str(upstream),
                    "branch": "master",
                    "commit": base_sha,
                    "tag": "v0.8.7",
                    "license": "GPL-3.0-or-later",
                },
                "patches": {
                    "dir": "patches",
                    "seriesFile": "patches/series",
                    "series": [],
                },
            },
            lock_path,
        )
        return upstream, lock_path, patches_dir, series_file, base_sha

    def test_empty_series_round_trip(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            upstream, lock_path, patches_dir, series_file, _ = self._init_repo_and_lock(Path(tmp))
            res = verify_mod.verify_upstream_checkout(
                upstream, lock_path=lock_path, patches_dir=patches_dir, series_file=series_file
            )
            self.assertEqual(res["patchCount"], 0)
            self.assertEqual(apply_mod.apply_patches(upstream, lock_path=lock_path, patches_dir=patches_dir, series_file=series_file), 0)

    def test_export_apply_and_verify_patched_round_trip(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            upstream, lock_path, patches_dir, series_file, base_sha = self._init_repo_and_lock(Path(tmp))
            (upstream / "README.md").write_text("base\nhost tweak\n", encoding="utf-8")
            _git(upstream, "commit", "-am", "适配宿主编译参数")

            names = export_mod.export_patches(
                upstream, lock_path=lock_path, patches_dir=patches_dir, series_file=series_file
            )
            self.assertEqual(len(names), 1)
            self.assertTrue((patches_dir / names[0]).is_file())
            lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
            self.assertEqual(lock_data["patches"]["series"], names)

            # 退回基线后重放并做已打补丁检查
            _git(upstream, "reset", "--hard", base_sha)
            applied = apply_mod.apply_patches(
                upstream, lock_path=lock_path, patches_dir=patches_dir, series_file=series_file
            )
            self.assertEqual(applied, 1)
            res = verify_mod.verify_upstream_checkout(
                upstream,
                lock_path=lock_path,
                patches_dir=patches_dir,
                series_file=series_file,
                patched=True,
            )
            self.assertEqual(res["patchCount"], 1)

    def test_rejects_dirty_working_tree(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            upstream, lock_path, patches_dir, series_file, _ = self._init_repo_and_lock(Path(tmp))
            (upstream / "untracked.txt").write_text("dirty\n", encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "不干净"):
                verify_mod.verify_upstream_checkout(
                    upstream, lock_path=lock_path, patches_dir=patches_dir, series_file=series_file
                )


if __name__ == "__main__":
    unittest.main()
