#!/usr/bin/env python3
"""测试 check-host-manifest.py 的正向与负向校验规则。"""

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("check_manifest", TOOLS_DIR / "check-host-manifest.py")
assert SPEC and SPEC.loader
check_manifest = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(check_manifest)

LOCK_PATH = TOOLS_DIR.parents[1] / "engine" / "openshiina" / "source-lock.json"

VALID_MANIFEST = """<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
    package="com.tyranor.next.shiinario">
  <application android:name="com.tyranor.next.shiinario.TyranorShiinaRioApplication">
    <activity
        android:name="com.tyranor.next.shiinario.ShiinaRioLaunchActivity"
        android:exported="true"
        android:launchMode="singleTask">
      <intent-filter>
        <action android:name="com.tyranor.next.action.LAUNCH_SHIINARIO" />
        <category android:name="android.intent.category.DEFAULT" />
      </intent-filter>
    </activity>
    <activity
        android:name="com.tyranor.next.shiinario.TyranorShiinaRioActivity"
        android:exported="false"
        android:process=":game" />
  </application>
</manifest>
"""


class HostManifestTests(unittest.TestCase):
    def test_valid_manifest_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "AndroidManifest.xml"
            path.write_text(VALID_MANIFEST, encoding="utf-8")
            check_manifest.check_manifest_xml(path, LOCK_PATH)

    def test_rejects_launcher_category(self) -> None:
        bad = VALID_MANIFEST.replace(
            '<category android:name="android.intent.category.DEFAULT" />',
            '<category android:name="android.intent.category.LAUNCHER" />',
        )
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "AndroidManifest.xml"
            path.write_text(bad, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "LAUNCHER"):
                check_manifest.check_manifest_xml(path, LOCK_PATH)

    def test_rejects_exported_runtime_activity(self) -> None:
        bad = VALID_MANIFEST.replace('android:exported="false"', 'android:exported="true"')
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "AndroidManifest.xml"
            path.write_text(bad, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "exported"):
                check_manifest.check_manifest_xml(path, LOCK_PATH)

    def test_rejects_missing_game_process(self) -> None:
        bad = VALID_MANIFEST.replace('android:process=":game"', 'android:process=":main"')
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "AndroidManifest.xml"
            path.write_text(bad, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, ":game"):
                check_manifest.check_manifest_xml(path, LOCK_PATH)


if __name__ == "__main__":
    unittest.main()
