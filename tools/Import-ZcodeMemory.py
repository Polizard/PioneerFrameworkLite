#!/usr/bin/env python3
"""Import the Zcode project memory into the DSH-owned AgentMemory mirror.

The Zcode memory is a flat directory of Markdown files (MEMORY.md index +
topic files).  This tool takes a stable snapshot of that directory and mirrors
it into ``AgentMemory/`` next to this repository, writing ``MANIFEST.json``
with the source hashes of the imported snapshot.

Default mode is a dry run.  ``--apply`` copies new files and files that are
still byte-identical to the last imported snapshot.  Any file that differs
from both the source and the last snapshot is a *conflict* (DSH edited it, or
the file was created by a different writer) and aborts the import unless
``--force`` is given.

Usage:
    python tools/Import-ZcodeMemory.py                  # dry run
    python tools/Import-ZcodeMemory.py --apply          # initial import / safe delta
    python tools/Import-ZcodeMemory.py --apply --force  # overwrite conflicts
    python tools/Import-ZcodeMemory.py --check-links    # report broken links

Exit codes: 0 = ok, 2 = conflicts, 3 = source kept changing, 4 = verify error.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import time
from datetime import datetime
from pathlib import Path
from urllib.parse import unquote

REPO = Path(__file__).resolve().parents[1]
SRC_DEFAULT = Path(
    os.path.expandvars(
        r"%USERPROFILE%\.zcode\cli\memories\projects"
        r"\pioneerframeworklite-74333f17f771f291\memory"
    )
)
DEST_DEFAULT = REPO / "AgentMemory"
MANIFEST_NAME = "MANIFEST.json"
META_NAMES = {"README.md", MANIFEST_NAME}


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def scan(root: Path) -> dict[str, dict]:
    """Hash every regular file directly under *root* (flat memory layout)."""
    out: dict[str, dict] = {}
    for path in sorted(root.iterdir()):
        if not path.is_file() or path.name.startswith("."):
            continue
        st = path.stat()
        out[path.name] = {
            "sha256": sha256_file(path),
            "size": st.st_size,
            "mtime": datetime.fromtimestamp(st.st_mtime).astimezone().isoformat(timespec="seconds"),
        }
    return out


def stable_scan(root: Path, tries: int = 4, pause: float = 0.25) -> dict[str, dict]:
    """Scan twice, retrying while the source is being written by another process."""
    previous = scan(root)
    for _ in range(tries - 1):
        time.sleep(pause)
        current = scan(root)
        if current == previous:
            return current
        previous = current
    raise RuntimeError("source directory kept changing during the snapshot")


def aggregate(files: dict[str, dict]) -> str:
    h = hashlib.sha256()
    for name in sorted(files):
        h.update(f"{name}\t{files[name]['sha256']}\n".encode("utf-8"))
    return h.hexdigest()


def load_manifest(dest: Path) -> dict:
    path = dest / MANIFEST_NAME
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def classify(source: dict[str, dict], dest: Path, manifest: dict) -> dict[str, list[str]]:
    last = {name: info.get("sha256") for name, info in manifest.get("files", {}).items()}
    result = {"new": [], "same": [], "update": [], "conflict": [], "orphan": []}
    for name, info in source.items():
        target = dest / name
        if not target.exists():
            result["new"].append(name)
            continue
        current = sha256_file(target)
        if current == info["sha256"]:
            result["same"].append(name)
        elif last.get(name) == current:
            # Dest is still exactly the last imported copy -> safe to refresh.
            result["update"].append(name)
        else:
            result["conflict"].append(name)
    for path in sorted(dest.glob("*.md")):
        if path.name not in source and path.name not in META_NAMES:
            result["orphan"].append(path.name)
    return result


def write_manifest(dest: Path, source_dir: Path, source: dict[str, dict]) -> Path:
    payload = {
        "schema": 1,
        "source": str(source_dir),
        "dest": str(dest),
        "imported_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "file_count": len(source),
        "total_bytes": sum(item["size"] for item in source.values()),
        "aggregate_sha256": aggregate(source),
        "files": source,
    }
    path = dest / MANIFEST_NAME
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return path


def copy_verified(src_dir: Path, dest: Path, names: list[str]) -> None:
    for name in names:
        shutil.copy2(src_dir / name, dest / name)
    for name in names:
        if sha256_file(dest / name) != sha256_file(src_dir / name):
            raise RuntimeError(f"verify failed after copy: {name}")


def check_links(root: Path) -> list[tuple[str, str]]:
    missing: list[tuple[str, str]] = []
    for path in sorted(root.glob("*.md")):
        if path.name in META_NAMES:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        text = re.sub(r"```.*?```", "", text, flags=re.S)
        for match in re.finditer(r"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)", text):
            target = match.group(1).strip()
            if target.startswith(("http://", "https://", "mailto:", "#")):
                continue
            target = unquote(target.split("#", 1)[0])
            if not target:
                continue
            if not (path.parent / target).resolve().exists():
                missing.append((path.name, target))
        for match in re.finditer(r"\[\[([^\]|#]+)(?:[|#][^\]]*)?\]\]", text):
            name = match.group(1).strip()
            if not (path.parent / f"{name}.md").exists():
                missing.append((path.name, f"[[{name}]]"))
    # De-duplicate while preserving order.
    seen: set[tuple[str, str]] = set()
    unique: list[tuple[str, str]] = []
    for item in missing:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def main() -> int:
    parser = argparse.ArgumentParser(description="Mirror Zcode project memory into AgentMemory/.")
    parser.add_argument("--source", default=str(SRC_DEFAULT), help="Zcode memory directory")
    parser.add_argument("--dest", default=str(DEST_DEFAULT), help="DSH memory mirror directory")
    parser.add_argument("--apply", action="store_true", help="write the mirror (default: dry run)")
    parser.add_argument("--force", action="store_true", help="overwrite conflicts (discards DSH edits)")
    parser.add_argument("--check-links", action="store_true", help="report broken Markdown/wiki links")
    args = parser.parse_args()

    src = Path(args.source).expanduser().resolve()
    dest = Path(args.dest).expanduser().resolve()
    if not src.is_dir():
        print(f"ERROR: source directory not found: {src}")
        return 3

    try:
        source = stable_scan(src)
    except RuntimeError as exc:
        print(f"ERROR: {exc}")
        return 3

    dest.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(dest)
    groups = classify(source, dest, manifest)

    total_bytes = sum(item["size"] for item in source.values())
    print(f"source : {src}")
    print(f"dest   : {dest}")
    print(f"files  : {len(source)}  bytes: {total_bytes}  aggregate: {aggregate(source)[:16]}...")
    print(
        "status : "
        f"new={len(groups['new'])} same={len(groups['same'])} "
        f"update={len(groups['update'])} conflict={len(groups['conflict'])} "
        f"orphan={len(groups['orphan'])}"
    )
    for label in ("new", "update", "conflict", "orphan"):
        if groups[label]:
            preview = ", ".join(groups[label][:12])
            suffix = "" if len(groups[label]) <= 12 else f", ... (+{len(groups[label]) - 12})"
            print(f"  {label:8}: {preview}{suffix}")

    if groups["conflict"] and not args.force:
        print("ABORT: conflicts detected. Resolve them manually, or rerun with --force to overwrite.")
        return 2

    if not args.apply:
        print("DRY RUN: nothing written. Re-run with --apply to update the mirror.")
    else:
        to_copy = groups["new"] + groups["update"]
        if args.force:
            to_copy += groups["conflict"]
        if to_copy:
            copy_verified(src, dest, to_copy)
        write_manifest(dest, src, source)
        print(f"APPLIED: copied {len(to_copy)} file(s); manifest written.")

    if args.check_links:
        scan_root = dest if any(dest.glob("*.md")) else src
        missing = check_links(scan_root)
        print(f"links  : {len(missing)} missing target(s) under {scan_root}")
        for doc, target in missing[:40]:
            print(f"  {doc} -> {target}")
        if len(missing) > 40:
            print(f"  ... (+{len(missing) - 40} more)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
