#!/usr/bin/env python3
"""测试 export-catalog.py 的生成、通用方案过滤与冲突检测。"""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("export_catalog_test", TOOLS_DIR / "export-catalog.py")
assert SPEC and SPEC.loader
export_catalog = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(export_catalog)


class ExportCatalogTests(unittest.TestCase):
    def _write_fixture(self, root: Path, game_map: dict[str, str]) -> tuple[Path, Path]:
        formats_path = root / "src" / "OpenShiina.Core" / "Data" / "Formats.Json"
        formats_path.parent.mkdir(parents=True)
        formats_path.write_text(
            json.dumps(
                {
                    "Version": 149,
                    "SchemeMap": {
                        "WAR": {
                            "KnownSchemes": [
                                {"Name": "Aneiro", "OriginalTitle": "アネイロ", "Version": 2490},
                                {"Name": "ShiinaRio v2.34 and older", "Version": 2340},
                                {"Name": "ShiinaRio v2.36-2.38", "Version": 2380},
                            ]
                        }
                    },
                    "GameMap": game_map,
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        lock_path = root / "source-lock.json"
        lock_path.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "engine": "openshiina",
                    "upstream": {
                        "repo": "https://example.invalid/OpenShiina.git",
                        "branch": "master",
                        "commit": "a" * 40,
                        "tag": "v0.8.7",
                    },
                    "catalog": {
                        "sourcePath": "src/OpenShiina.Core/Data/Formats.Json",
                    },
                }
            ),
            encoding="utf-8",
        )
        return root, lock_path

    def test_generates_deterministic_catalog_and_checks_match(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root, lock_path = self._write_fixture(Path(tmp), {"ANEIRO.exe": "Aneiro"})
            out = Path(tmp) / "games.json"
            payload = export_catalog.export_catalog(root, lock_path=lock_path, output_path=out)
            self.assertEqual([s["name"] for s in payload["schemes"]], ["Aneiro"])
            self.assertEqual(payload["gameMap"], {"aneiro.exe": "Aneiro"})
            self.assertIn("ShiinaRio v2.34 and older", payload["genericSchemes"])
            export_catalog.export_catalog(root, lock_path=lock_path, output_path=out, check_only=True)

    def test_rejects_generic_scheme_in_game_map(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root, lock_path = self._write_fixture(
                Path(tmp), {"GAME.exe": "ShiinaRio v2.34 and older"}
            )
            with self.assertRaisesRegex(ValueError, "通用兜底方案"):
                export_catalog.export_catalog(root, lock_path=lock_path, output_path=Path(tmp) / "out.json")

    def test_rejects_case_collision_in_game_map(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root, lock_path = self._write_fixture(
                Path(tmp), {"Game.exe": "Aneiro", "game.EXE": "Aneiro"}
            )
            with self.assertRaisesRegex(ValueError, "大小写折叠冲突"):
                export_catalog.export_catalog(root, lock_path=lock_path, output_path=Path(tmp) / "out.json")


if __name__ == "__main__":
    unittest.main()
