#!/usr/bin/env python3
"""从 OpenShiina 上游 Formats.Json 导出轻量化 games.json 游戏目录清单。"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from openshiina_common import (  # noqa: E402
    GAMES_CATALOG_PATH,
    GENERIC_SCHEME_NAMES,
    LOCK_PATH,
    load_lock,
    write_lf_utf8,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="从 OpenShiina Formats.Json 生成 games.json")
    parser.add_argument(
        "--source",
        required=True,
        type=Path,
        help="外部 OpenShiina 上游源码根目录（用于定位 Formats.Json）",
    )
    parser.add_argument(
        "--lock",
        type=Path,
        default=LOCK_PATH,
        help="source-lock.json 路径",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=GAMES_CATALOG_PATH,
        help="输出 games.json 路径",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="仅校验现有 games.json 与上游 Formats.Json 生成结果字节级一致",
    )
    return parser.parse_args(argv)


def build_catalog_payload(formats_path: Path, lock: dict[str, Any]) -> dict[str, Any]:
    """读取 Formats.Json 并构建确定性轻量化游戏目录结构。"""
    if not formats_path.is_file():
        raise FileNotFoundError(f"找不到上游方案数据文件：{formats_path}")

    raw = json.loads(formats_path.read_text(encoding="utf-8"))
    war = raw.get("SchemeMap", {}).get("WAR", {})
    known_schemes = war.get("KnownSchemes", [])
    raw_game_map = raw.get("GameMap", {})

    if not isinstance(known_schemes, list) or not isinstance(raw_game_map, dict):
        raise ValueError("Formats.Json 结构无效：缺少 SchemeMap.WAR.KnownSchemes 或 GameMap")

    scheme_meta: dict[str, dict[str, Any]] = {}
    generic_present: list[str] = []
    for item in known_schemes:
        name = item.get("Name")
        if not name or not isinstance(name, str):
            continue
        if name in GENERIC_SCHEME_NAMES:
            generic_present.append(name)
            continue
        scheme_meta[name] = {
            "name": name,
            "originalTitle": item.get("OriginalTitle") or name,
            "version": item.get("Version"),
        }

    executables_by_scheme: dict[str, list[str]] = defaultdict(list)
    normalized_game_map: dict[str, str] = {}

    for raw_key, scheme_name in raw_game_map.items():
        if not isinstance(raw_key, str) or not isinstance(scheme_name, str):
            continue
        if scheme_name in GENERIC_SCHEME_NAMES:
            raise ValueError(f"GameMap 不应直接映射通用兜底方案：{raw_key!r} -> {scheme_name!r}")
        if scheme_name not in scheme_meta:
            raise ValueError(f"GameMap 引用了未知方案：{raw_key!r} -> {scheme_name!r}")
        lowered = raw_key.strip().lower()
        if lowered in normalized_game_map:
            raise ValueError(f"GameMap 存在大小写折叠冲突键：{raw_key!r}")
        normalized_game_map[lowered] = scheme_name
        executables_by_scheme[scheme_name].append(raw_key.strip())

    schemes_out: list[dict[str, Any]] = []
    for name in sorted(scheme_meta.keys(), key=lambda s: (s.lower(), s)):
        meta = scheme_meta[name]
        exes = sorted(executables_by_scheme.get(name, []), key=lambda s: (s.lower(), s))
        schemes_out.append(
            {
                "name": meta["name"],
                "originalTitle": meta["originalTitle"],
                "version": meta["version"],
                "executables": exes,
            }
        )

    sorted_game_map = {
        key: normalized_game_map[key]
        for key in sorted(normalized_game_map.keys())
    }

    upstream = lock.get("upstream", {})
    return {
        "schemaVersion": 1,
        "engine": "openshiina",
        "formatsVersion": raw.get("Version"),
        "upstreamCommit": upstream.get("commit", ""),
        "upstreamTag": upstream.get("tag", ""),
        "genericSchemes": sorted(set(generic_present) | GENERIC_SCHEME_NAMES),
        "schemes": schemes_out,
        "gameMap": sorted_game_map,
    }


def serialize_catalog(payload: dict[str, Any]) -> str:
    """以确定性 UTF-8 LF 格式序列化 games.json。"""
    return json.dumps(payload, ensure_ascii=False, indent=2) + "\n"


def export_catalog(
    source_dir: Path,
    *,
    lock_path: Path = LOCK_PATH,
    output_path: Path = GAMES_CATALOG_PATH,
    check_only: bool = False,
) -> dict[str, Any]:
    """生成或校验 games.json。"""
    lock = load_lock(lock_path)
    rel_formats = lock.get("catalog", {}).get("sourcePath", "src/OpenShiina.Core/Data/Formats.Json")
    formats_path = source_dir / rel_formats
    payload = build_catalog_payload(formats_path, lock)
    expected_text = serialize_catalog(payload)

    if check_only:
        if not output_path.is_file():
            raise FileNotFoundError(f"缺少游戏目录清单文件：{output_path}")
        actual_text = output_path.read_text(encoding="utf-8")
        if actual_text != expected_text:
            raise RuntimeError(
                f"{output_path} 与上游 Formats.Json 不一致，请运行 python tools/openshiina/export-catalog.py --source <OpenShiina> 重新生成。"
            )
    else:
        write_lf_utf8(output_path, expected_text)

    return payload


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        payload = export_catalog(
            args.source.resolve(),
            lock_path=args.lock.resolve(),
            output_path=args.output.resolve(),
            check_only=args.check,
        )
    except Exception as exc:  # noqa: BLE001
        print(f"[export-catalog] FAILED: {exc}", file=sys.stderr)
        return 1

    mode = "CHECK OK" if args.check else "GENERATED"
    print(
        f"[export-catalog] {mode}: {len(payload['schemes'])} schemes, "
        f"{len(payload['gameMap'])} mapped files (Formats v{payload['formatsVersion']})."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
