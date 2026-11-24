#!/usr/bin/env python3
"""测试 sync-upstream.py 使用本地远端推进提交、重放补丁并更新 source-lock 与 games.json。"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
import sys

sys.path.insert(0, str(TOOLS_DIR))
from openshiina_common import save_lock, save_series_file  # noqa: E402

SPEC = importlib.util.spec_from_file_location("sync_upstream_test", TOOLS_DIR / "sync-upstream.py")
assert SPEC and SPEC.loader
sync_mod = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(sync_mod)


def _git(repo: Path, *args: str) -> str:
    proc = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        text=True,
        encoding="utf-8",
        capture_output=True,
    )
    return proc.stdout.strip()


class SyncUpstreamTests(unittest.TestCase):
    def test_syncs_new_commit_tag_and_catalog(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            remote = root / "remote"
            remote.mkdir()
            _git(remote, "init", "-b", "master")
            _git(remote, "config", "user.email", "test@example.com")
            _git(remote, "config", "user.name", "Test User")
            formats = remote / "src" / "OpenShiina.Core" / "Data" / "Formats.Json"
            formats.parent.mkdir(parents=True)
            formats.write_text(
                json.dumps(
                    {
                        "Version": 149,
                        "SchemeMap": {
                            "WAR": {"KnownSchemes": [{"Name": "Aneiro", "Version": 2490}]}
                        },
                        "GameMap": {"ANEIRO.exe": "Aneiro"},
                    }
                ),
                encoding="utf-8",
            )
            _git(remote, "add", ".")
            _git(remote, "commit", "-m", "v0.8.7")
            _git(remote, "tag", "v0.8.7")
            old_sha = _git(remote, "rev-parse", "HEAD")

            # 在远端新增一个上游提交与新标签
            formats.write_text(
                json.dumps(
                    {
                        "Version": 150,
                        "SchemeMap": {
                            "WAR": {
                                "KnownSchemes": [
                                    {"Name": "Aneiro", "Version": 2490},
                                    {"Name": "Tsukutori", "OriginalTitle": "月鳥", "Version": 2490},
                                ]
                            }
                        },
                        "GameMap": {"ANEIRO.exe": "Aneiro", "tsukutori.exe": "Tsukutori"},
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            _git(remote, "commit", "-am", "v0.8.8")
            _git(remote, "tag", "v0.8.8")
            new_sha = _git(remote, "rev-parse", "HEAD")

            patches_dir = root / "patches"
            series_file = patches_dir / "series"
            save_series_file([], series_file)
            lock_path = root / "source-lock.json"
            save_lock(
                {
                    "schemaVersion": 1,
                    "engine": "openshiina",
                    "upstream": {
                        "repo": str(remote),
                        "branch": "master",
                        "commit": old_sha,
                        "tag": "v0.8.7",
                        "license": "GPL-3.0-or-later",
                    },
                    "patches": {"dir": "patches", "seriesFile": "patches/series", "series": []},
                    "host": {"versionCode": 1, "versionName": "0.1.0"},
                    "catalog": {"sourcePath": "src/OpenShiina.Core/Data/Formats.Json"},
                },
                lock_path,
            )
            catalog_path = root / "games.json"
            checkout = root / "checkout"

            summary = sync_mod.sync_upstream(
                checkout,
                lock_path=lock_path,
                patches_dir=patches_dir,
                series_file=series_file,
                catalog_path=catalog_path,
            )
            self.assertTrue(summary["changed"])
            self.assertEqual(summary["newCommit"], new_sha)
            self.assertEqual(summary["newTag"], "v0.8.8")
            self.assertEqual(summary["schemeCount"], 2)

            saved_lock = json.loads(lock_path.read_text(encoding="utf-8"))
            self.assertEqual(saved_lock["upstream"]["commit"], new_sha)
            self.assertEqual(saved_lock["upstream"]["tag"], "v0.8.8")

            saved_catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
            self.assertEqual(saved_catalog["formatsVersion"], 150)
            self.assertIn("tsukutori.exe", saved_catalog["gameMap"])

    def test_syncs_release_tag_updates_version_name_and_code(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            remote = root / "remote"
            remote.mkdir()
            _git(remote, "init", "-b", "master")
            _git(remote, "config", "user.email", "test@example.com")
            _git(remote, "config", "user.name", "Test User")
            formats = remote / "src" / "OpenShiina.Core" / "Data" / "Formats.Json"
            formats.parent.mkdir(parents=True)
            formats.write_text(
                json.dumps(
                    {
                        "Version": 149,
                        "SchemeMap": {
                            "WAR": {"KnownSchemes": [{"Name": "Aneiro", "Version": 2490}]}
                        },
                        "GameMap": {"ANEIRO.exe": "Aneiro"},
                    }
                ),
                encoding="utf-8",
            )
            _git(remote, "add", ".")
            _git(remote, "commit", "-m", "v0.8.7")
            _git(remote, "tag", "v0.8.7")
            sha = _git(remote, "rev-parse", "HEAD")

            patches_dir = root / "patches"
            series_file = patches_dir / "series"
            save_series_file([], series_file)
            lock_path = root / "source-lock.json"
            save_lock(
                {
                    "schemaVersion": 1,
                    "engine": "openshiina",
                    "upstream": {
                        "repo": str(remote),
                        "branch": "master",
                        "commit": sha,
                        "tag": "v0.8.7",
                        "license": "GPL-3.0-or-later",
                    },
                    "patches": {"dir": "patches", "seriesFile": "patches/series", "series": []},
                    "host": {"versionCode": 1, "versionName": "0.1.0"},
                    "catalog": {"sourcePath": "src/OpenShiina.Core/Data/Formats.Json"},
                },
                lock_path,
            )
            catalog_path = root / "games.json"
            checkout = root / "checkout"

            same_ver = sync_mod.sync_upstream(
                checkout,
                lock_path=lock_path,
                patches_dir=patches_dir,
                series_file=series_file,
                catalog_path=catalog_path,
                release_tag="v0.1.0",
            )
            self.assertFalse(same_ver["versionChanged"])
            self.assertEqual(same_ver["versionCode"], 1)
            self.assertEqual(same_ver["versionName"], "0.1.0")

            bumped = sync_mod.sync_upstream(
                checkout,
                lock_path=lock_path,
                patches_dir=patches_dir,
                series_file=series_file,
                catalog_path=catalog_path,
                release_tag="v0.2.0",
            )
            self.assertTrue(bumped["versionChanged"])
            self.assertTrue(bumped["changed"])
            self.assertEqual(bumped["versionCode"], 2)
            self.assertEqual(bumped["versionName"], "0.2.0")

            saved_lock = json.loads(lock_path.read_text(encoding="utf-8"))
            self.assertEqual(saved_lock["host"]["versionCode"], 2)
            self.assertEqual(saved_lock["host"]["versionName"], "0.2.0")


if __name__ == "__main__":
    unittest.main()
