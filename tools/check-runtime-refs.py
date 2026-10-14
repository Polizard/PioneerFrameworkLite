#!/usr/bin/env python3
"""Runtime-binding auditor for the 1.7 migration (2026-10-13).

WHY: some gaps are invisible to BOTH the compiler and the net48 build --
the missing member is *declared on a different type* than in 1.6
(measured case: Texture.isDataSRGB -> in 1.6 Unity it is on Texture, in
UnityStubs it is on Texture2D; the net48-compiled DLL emits a MemberRef to
`Texture::get_isDataSRGB` and the JIT throws MissingMethodException at runtime).

This script replays every Unity MemberRef our net48 build actually emits
(`ApiDump refs`) against UnityStubs' member table, resolving through the
inheritance chain like the runtime does, and prints everything that would fail.

Inputs:
  our_unity_refs.tsv     -- ApiDump refs <our dll> UnityEngine
  unitystubs_members.tsv -- ApiDump nsmembers <UnityStubs.dll> UnityEngine

Usage: python tools/check-runtime-refs.py   (run from repo root)
Exit:  0 = no hazard / 1 = hazards listed
"""
import sys
from pathlib import Path

TMP = Path("D:/tmp/rw17")
REFS = TMP / "our_unity_refs.tsv"
TSV = TMP / "unitystubs_members.tsv"

# Inheritance chains (UnityStubs mirrors Unity's hierarchy; only the types our
# member refs actually touch need an entry). Missing entry => reported as UNKNOWN.
CHAINS = {
    "UnityEngine.Object": [],
    "UnityEngine.Component": ["UnityEngine.Object"],
    "UnityEngine.Behaviour": ["UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.MonoBehaviour": ["UnityEngine.Behaviour", "UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.Transform": ["UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.Camera": ["UnityEngine.Behaviour", "UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.Light": ["UnityEngine.Behaviour", "UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.GameObject": ["UnityEngine.Object"],
    "UnityEngine.Mesh": ["UnityEngine.Object"],
    "UnityEngine.Material": ["UnityEngine.Object"],
    "UnityEngine.Shader": ["UnityEngine.Object"],
    "UnityEngine.Texture": ["UnityEngine.Object"],
    "UnityEngine.Texture2D": ["UnityEngine.Texture", "UnityEngine.Object"],
    "UnityEngine.RenderTexture": ["UnityEngine.Texture", "UnityEngine.Object"],
    "UnityEngine.Renderer": ["UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.MeshFilter": ["UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.MeshRenderer": ["UnityEngine.Renderer", "UnityEngine.Component", "UnityEngine.Object"],
    "UnityEngine.AssetBundle": ["UnityEngine.Object"],
    "UnityEngine.Font": ["UnityEngine.Object"],
    "UnityEngine.ScriptableObject": ["UnityEngine.Object"],
    "UnityEngine.MaterialPropertyBlock": [],
    "UnityEngine.Quaternion": [],
    "UnityEngine.Vector2": [], "UnityEngine.Vector3": [], "UnityEngine.Vector4": [],
    "UnityEngine.Color": [], "UnityEngine.Color32": [],
    "UnityEngine.Rect": [], "UnityEngine.Matrix4x4": [], "UnityEngine.LayerMask": [],
    "UnityEngine.Mathf": [], "UnityEngine.Graphics": [], "UnityEngine.GL": [],
    "UnityEngine.GUI": [], "UnityEngine.Event": [], "UnityEngine.Input": [],
    "UnityEngine.Time": [], "UnityEngine.Debug": [], "UnityEngine.Random": [],
    "UnityEngine.Application": [], "UnityEngine.Screen": [],
    "UnityEngine.QualitySettings": [], "UnityEngine.SystemInfo": [],
    "UnityEngine.Physics": [], "UnityEngine.Ray": [], "UnityEngine.Bounds": [],
    "UnityEngine.Rendering.CommandBuffer": [],
    "UnityEngine.Rendering.RenderTargetIdentifier": [],
    "UnityEngine.ImageConversion": [],
    "UnityEngine.Camera.CameraCallback": [],
}

# Parents that are NOT Unity types: skipped with a written reason.
# JetBrains.Annotations: attribute applications are metadata-only and resolved lazily;
# the same DLL ran on 1.7 with zero attribute-related errors (measured 23:14 run log).
IGNORED_PARENTS = {
    "JetBrains.Annotations.UsedImplicitlyAttribute",
}


def load_provided(path):
    provided = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split("\t")
        if not parts:
            continue
        if parts[0] == "T" and len(parts) >= 3:
            provided.setdefault(parts[2], set())
        elif parts[0] in ("M", "F") and len(parts) >= 3:
            provided.setdefault(parts[1], set()).add(parts[2])
        elif parts[0] == "P" and len(parts) >= 4:
            declared = provided.setdefault(parts[1], set())
            declared.add(parts[2])
            if "get" in parts[3]:
                declared.add("get_" + parts[2])
            if "set" in parts[3]:
                declared.add("set_" + parts[2])
    return provided


def main():
    if not REFS.exists() or not TSV.exists():
        print(f"missing input: {REFS} / {TSV}")
        return 2
    provided = load_provided(TSV)
    hazards = {}
    unknown = set()
    total = 0
    for line in REFS.read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split("\t")
        if len(parts) < 5 or parts[0] != "RM":
            continue
        name, parent = parts[3], parts[2]
        total += 1
        if parent in IGNORED_PARENTS:
            continue
        if parent not in CHAINS:
            unknown.add(parent)
            continue
        ok = False
        for t in [parent] + CHAINS[parent]:
            if name in provided.get(t, set()):
                ok = True
                break
        if not ok:
            hazards.setdefault((parent, name), 0)
            hazards[(parent, name)] += 1

    print(f"refs checked: {total}")
    if unknown:
        print("UNKNOWN parent types (add a chain entry, then re-run):")
        for t in sorted(unknown):
            print("   ", t)
    if hazards:
        print("HAZARDS (would throw MissingMethod/FieldException at runtime on 1.7):")
        for (parent, name), n in sorted(hazards.items()):
            print(f"    {parent}::{name}   (x{n})")
        return 1
    print("all refs resolve against UnityStubs (with inheritance).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
