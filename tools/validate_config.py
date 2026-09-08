#!/usr/bin/env python3
"""PioneerFrameworkLite config validation.

Checks:
- XML under 1.6/Defs and 1.6/Patches is well-formed
- every PFDefs [DefOf] field has a matching defName in Defs
- About.xml declares a valid packageId, supportedVersions 1.6 and Harmony dependency
- committed AssetBundles and Shader sources exist
- optional Languages Keyed/DefInjected consistency (when Languages/ exists)
- optional LoadFolders.xml well-formedness (when present)
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def fail(msg):
    print(f"FAIL: {msg}")
    sys.exit(1)


def main():
    # 1. XML well-formed + collect defNames
    defs_dir = ROOT / "1.6" / "Defs"
    if not defs_dir.exists():
        fail("1.6/Defs directory not found")
    def_names = set()
    xml_roots = []
    for xml_path in sorted(defs_dir.rglob("*.xml")):
        try:
            root = ET.parse(xml_path).getroot()
        except ET.ParseError as e:
            fail(f"{xml_path.relative_to(ROOT)}: XML parse error: {e}")
        xml_roots.append((xml_path, root))
        for child in root:
            def_name = child.findtext("defName")
            if def_name:
                def_name = def_name.strip()
                def_names.add(def_name)
                # Rule 1: ThingDef defName must not end with a numerical digit.
                if child.tag == "ThingDef" and def_name[-1].isdigit():
                    fail(
                        f"{xml_path.relative_to(ROOT)}: ThingDef defName '{def_name}' "
                        "ends with a numerical digit, which is not allowed on ThingDefs."
                    )

    patches_dir = ROOT / "1.6" / "Patches"
    if patches_dir.exists():
        for xml_path in sorted(patches_dir.rglob("*.xml")):
            try:
                ET.parse(xml_path)
            except ET.ParseError as e:
                fail(f"{xml_path.relative_to(ROOT)}: XML parse error: {e}")

    # 2. PFDefs fields must exist in Defs
    defof_path = ROOT / "Source" / "PioneerFramework" / "PFDefs.cs"
    if not defof_path.exists():
        fail(f"missing {defof_path.relative_to(ROOT)}")
    text = defof_path.read_text(encoding="utf-8")
    fields = re.findall(
        r'public static\s+[A-Za-z0-9_.<>\[\],\?]+\s+(\w+)\s*(?:=|;)',
        text)
    missing = [name for name in fields if name not in def_names]
    if missing:
        fail(f"PFDefs references missing in Defs: {missing}")

    # 3. About.xml
    about_path = ROOT / "About" / "About.xml"
    if not about_path.exists():
        fail("About/About.xml not found")
    try:
        about = ET.parse(about_path).getroot()
    except ET.ParseError as e:
        fail(f"About/About.xml: XML parse error: {e}")
    package_id = about.findtext("packageId")
    if not package_id or package_id != "NewFrontierTeam.PioneerFrameworkLite":
        fail(f"About.xml packageId must be NewFrontierTeam.PioneerFrameworkLite, got {package_id!r}")
    supported = [li.text for li in about.findall("supportedVersions/li")]
    if "1.6" not in supported:
        fail(f"About.xml supportedVersions must include 1.6, got {supported}")
    dependencies = [
        li.findtext("packageId")
        for li in about.findall("modDependencies/li")
    ]
    if "brrainz.harmony" not in dependencies:
        fail(f"About.xml must declare brrainz.harmony dependency, got {dependencies}")

    # 4. Committed runtime assets
    required_assets = [
        "1.6/Assemblies/PioneerFrameworkLite.dll",
        "1.6/AssetBundles/pfview_outline_win",
        "1.6/AssetBundles/pfview_outline_mac",
        "1.6/AssetBundles/pfview_outline_linux",
        "Shaders/PFView_OutlineMask.shader",
        "Shaders/PFView_OutlineDilate.shader",
        "Shaders/PFView_OutlineComposite.shader",
    ]
    for rel in required_assets:
        if not (ROOT / rel).is_file():
            fail(f"required committed asset missing: {rel}")

    # 5. Optional Languages validation (mirrors RimPipe when languages exist)
    langs_dir = ROOT / "Languages"
    if langs_dir.exists():
        key_sets = {}
        for keyed_path in sorted(langs_dir.rglob("Keyed/*.xml")):
            try:
                root = ET.parse(keyed_path).getroot()
            except ET.ParseError as e:
                fail(f"{keyed_path.relative_to(ROOT)}: XML parse error: {e}")
            keys = set(child.tag for child in root)
            lang = keyed_path.parent.parent.name
            key_sets[lang] = keys

        required_langs = {"ChineseSimplified", "English"}
        if not required_langs.issubset(key_sets.keys()):
            fail(f"expected ChineseSimplified and English Keyed files, got {sorted(key_sets.keys())}")
        base = None
        for lang, keys in key_sets.items():
            if base is None:
                base = keys
            elif base != keys:
                only_a = sorted(base - keys)
                only_b = sorted(keys - base)
                fail(f"Keyed mismatch for {lang}: missing={only_a} extra={only_b}")

        definjected_sets = {}
        for definjected_path in sorted(
            p for p in langs_dir.rglob("*.xml") if "DefInjected" in p.parts
        ):
            try:
                root = ET.parse(definjected_path).getroot()
            except ET.ParseError as e:
                fail(f"{definjected_path.relative_to(ROOT)}: XML parse error: {e}")
            lang = definjected_path.parent.parent.parent.name
            keys = definjected_sets.setdefault(lang, set())
            for child in root:
                tag = child.tag
                if "." not in tag:
                    continue
                def_name = tag.split(".", 1)[0]
                if def_name not in def_names:
                    fail(
                        f"{definjected_path.relative_to(ROOT)}: DefInjected tag '{tag}' "
                        f"references missing DefName '{def_name}'"
                    )
                keys.add(tag)

        definjected_base = None
        for lang in sorted(definjected_sets):
            keys = definjected_sets[lang]
            if definjected_base is None:
                definjected_base = keys
            elif definjected_base != keys:
                only_a = sorted(definjected_base - keys)
                only_b = sorted(keys - definjected_base)
                fail(f"DefInjected mismatch for {lang}: missing={only_a} extra={only_b}")

    # 6. Optional LoadFolders validation
    load_folders = ROOT / "LoadFolders.xml"
    if load_folders.exists():
        try:
            ET.parse(load_folders)
        except ET.ParseError as e:
            fail(f"LoadFolders.xml: XML parse error: {e}")

    # 7. Rule 2: every graphicData/texPath must resolve to an actual texture.
    #    Check PFL 1.6/Textures first. Base-game textures are packed in Unity assets
    #    (no loose file on disk), so valid base references are tracked via an allowlist.
    KNOWN_BASE_TEX = {
        # 原版酿造桶贴图（打包在 Unity 资源里，无法用文件系统校验）
        "Things/Building/Production/FermentingBarrel",
        # 原版通讯台贴图（PFL 控制终端临时借用）
        "Things/Building/Misc/CommsConsole",
        # 原版洞穴出口贴图（PFL 火箭舱门临时借用）
        "Things/Building/Misc/CaveExit/CaveExit",
        # 原版大型子弹贴图（F3 炮塔射弹临时借用）
        "Things/Projectile/Bullet_Big",
        # 原版狙击炮塔顶盖贴图（PFL 加农炮塔临时借用）
        "Things/Building/Security/TurretSniper_Top",
        # 原版重型炮塔底座贴图（PFL 加农炮塔临时借用；ae1d4b7 引用但漏入清单，2026-09-09 补）
        "Things/Building/Security/TurretHeavy_Base",
        # 原版飞船传感器组件贴图（PFL 目标定位机临时借用）
        "Things/Building/Ship/ShipSensorCluster",
    }

    def texture_file(tex_path):
        local = ROOT / "1.6" / "Textures" / f"{tex_path}.png"
        if local.is_file():
            return local
        return None

    for xml_path, root in xml_roots:
        for child in root:
            tex = child.findtext("graphicData/texPath")
            if tex:
                tex = tex.strip()
                if texture_file(tex) is None and tex not in KNOWN_BASE_TEX:
                    fail(
                        f"{xml_path.relative_to(ROOT)}: def '{child.findtext('defName')}' "
                        f"references missing texture '{tex}' (not in 1.6/Textures or "
                        "known base textures)."
                    )


    print(
        f"OK: {len(def_names)} defs, {len(fields)} DefOf fields, "
        f"{len(xml_roots)} Def XML files, required assets present, "
        f"About.xml and optional Languages/LoadFolders checks passed."
    )


if __name__ == "__main__":
    main()
