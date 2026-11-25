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
  <application
      android:name="com.tyranor.next.shiinario.TyranorShiinaRioApplication"
      android:theme="@style/TyranorShiinaRioTheme">
    <activity
        android:name="com.tyranor.next.shiinario.ShiinaRioLaunchActivity"
        android:exported="true"
        android:launchMode="singleTask"
        android:theme="@android:style/Theme.Translucent.NoTitleBar">
      <intent-filter>
        <action android:name="com.tyranor.next.action.LAUNCH_SHIINARIO" />
        <category android:name="android.intent.category.DEFAULT" />
      </intent-filter>
    </activity>
    <activity
        android:name="com.tyranor.next.shiinario.TyranorShiinaRioActivity"
        android:exported="false"
        android:process=":game"
        android:theme="@style/TyranorShiinaRioTheme" />
  </application>
</manifest>
"""

VALID_STYLES = """<?xml version="1.0" encoding="utf-8"?>
<resources>
  <style name="TyranorShiinaRioTheme" parent="@style/Theme.AppCompat.NoActionBar">
    <item name="android:windowBackground">@android:color/black</item>
  </style>
</resources>
"""


def _write_host_files(
    root: Path,
    manifest_text: str = VALID_MANIFEST,
    styles_text: str | None = VALID_STYLES,
) -> tuple[Path, Path]:
    manifest_path = root / "Properties" / "AndroidManifest.xml"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(manifest_text, encoding="utf-8")

    styles_path = root / "Resources" / "values" / "styles.xml"
    if styles_text is not None:
        styles_path.parent.mkdir(parents=True, exist_ok=True)
        styles_path.write_text(styles_text, encoding="utf-8")
    return manifest_path, styles_path


class HostManifestTests(unittest.TestCase):
    def test_valid_manifest_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp))
            check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_valid_indirect_appcompat_parent_passes(self) -> None:
        styles = """<?xml version="1.0" encoding="utf-8"?>
<resources>
  <style name="BaseAppCompat" parent="@style/Theme.AppCompat.NoActionBar" />
  <style name="TyranorShiinaRioTheme" parent="@style/BaseAppCompat" />
</resources>
"""
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), styles_text=styles)
            check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_launcher_category(self) -> None:
        bad = VALID_MANIFEST.replace(
            '<category android:name="android.intent.category.DEFAULT" />',
            '<category android:name="android.intent.category.LAUNCHER" />',
        )
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, "LAUNCHER"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_exported_runtime_activity(self) -> None:
        bad = VALID_MANIFEST.replace('android:exported="false"', 'android:exported="true"')
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, "exported"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_missing_game_process(self) -> None:
        bad = VALID_MANIFEST.replace('android:process=":game"', 'android:process=":main"')
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, ":game"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_android_system_theme_on_runtime_activity(self) -> None:
        bad = VALID_MANIFEST.replace(
            '        android:process=":game"\n        android:theme="@style/TyranorShiinaRioTheme"',
            '        android:process=":game"\n        android:theme="@android:style/Theme.NoTitleBar.Fullscreen"',
        )
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, "Theme.AppCompat"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_missing_theme_on_runtime_activity(self) -> None:
        bad = VALID_MANIFEST.replace(
            '        android:process=":game"\n        android:theme="@style/TyranorShiinaRioTheme"',
            '        android:process=":game"',
        )
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, "android:theme"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_undefined_style_in_styles_xml(self) -> None:
        bad = VALID_MANIFEST.replace(
            '        android:process=":game"\n        android:theme="@style/TyranorShiinaRioTheme"',
            '        android:process=":game"\n        android:theme="@style/MissingTheme"',
        )
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, "MissingTheme"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_non_appcompat_parent_style(self) -> None:
        bad_styles = """<?xml version="1.0" encoding="utf-8"?>
<resources>
  <style name="TyranorShiinaRioTheme" parent="@android:style/Theme.Material.NoActionBar" />
</resources>
"""
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), styles_text=bad_styles)
            with self.assertRaisesRegex(ValueError, "Theme.AppCompat"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)

    def test_rejects_android_system_theme_on_application(self) -> None:
        bad = VALID_MANIFEST.replace(
            '      android:theme="@style/TyranorShiinaRioTheme">',
            '      android:theme="@android:style/Theme.NoTitleBar.Fullscreen">',
        )
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path, styles_path = _write_host_files(Path(tmp), manifest_text=bad)
            with self.assertRaisesRegex(ValueError, "application"):
                check_manifest.check_manifest_xml(manifest_path, LOCK_PATH, styles_path)


if __name__ == "__main__":
    unittest.main()
