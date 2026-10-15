#!/usr/bin/env python3
"""PioneerFrameworkLite config validation.

Checks:
- XML under every version package folder that exists (1.6/ and/or 1.7/) is well-formed
- every PFDefs [DefOf] field has a matching defName in Defs
- About.xml declares a valid packageId, the NEWEST existing version folder in supportedVersions,
  and the brrainz.harmony dependency
- committed per-version Assemblies exist (plus the frozen 1.6 bundles / shader sources)
- CelestialBodyDef XML elements must be declared C# fields (atmosphere-data guard, 2026-10-06)
- optional Languages Keyed/DefInjected consistency (when Languages/ exists)
- optional LoadFolders.xml well-formedness (when present)
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# Version package folders we validate. 2026-10-13 tree split: 1.6 is FROZEN, 1.7 is the mainline;
# both are validated whenever present, so this script works before and after the 1.7 package lands.
VERSION_DIRS = [d for d in (ROOT / "1.6", ROOT / "1.7") if d.exists()]


def fail(msg):
    print(f"FAIL: {msg}")
    sys.exit(1)


def main():
    # 1. XML well-formed + collect defNames (all existing version package folders)
    defs_dirs = [v / "Defs" for v in VERSION_DIRS if (v / "Defs").exists()]
    if not defs_dirs:
        fail("no <version>/Defs directory found (expected 1.6/Defs and/or 1.7/Defs)")
    def_names = set()
    xml_roots = []
    for defs_dir in defs_dirs:
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

    for patches_dir in (v / "Patches" for v in VERSION_DIRS):
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
    newest = VERSION_DIRS[-1].name  # "1.7" once its package exists, else "1.6"
    if newest not in supported:
        fail(f"About.xml supportedVersions must include the newest version folder ({newest}), got {supported}")
    dependencies = [
        li.findtext("packageId")
        for li in about.findall("modDependencies/li")
    ]
    if "brrainz.harmony" not in dependencies:
        fail(f"About.xml must declare brrainz.harmony dependency, got {dependencies}")

    # 4. Committed runtime assets: one Assemblies/DLL per existing version folder,
    #    plus the frozen 1.6 bundles / shader sources.
    required_assets = [f"{v.name}/Assemblies/PioneerFrameworkLite.dll" for v in VERSION_DIRS]
    required_assets += [
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
        for v in VERSION_DIRS:
            local = v / "Textures" / f"{tex_path}.png"
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

    # 7.5 ThingDef 自带 `graphicData` 的，必须**显式**写 `graphicClass`（texPath 也不能空）。
    #     WHY（2026-10-07 实测，**用户在游戏里撞见的**而闸门当时没查）：`graphicClass` 既不默认也不继承
    #     ⇒ 漏写就是启动时一行红字 `Config error in <def>: graphicClass is null`
    #     （`Verse.GraphicData.ConfigErrors` 报的）。同族：`texPath is null or empty`。
    #     教训：**RimWorld 自己的 ConfigErrors 是免费的检查清单** —— 能离线复刻的就要复刻进闸门。
    for xml_path, root in xml_roots:
        for child in root.iter("ThingDef"):
            gd = child.find("graphicData")
            if gd is None:
                continue
            name = (child.findtext("defName") or child.get("ParentName") or "?").strip()
            if not (gd.findtext("graphicClass") or "").strip():
                fail(
                    f"{xml_path.relative_to(ROOT)}: ThingDef '{name}' has graphicData but no "
                    "graphicClass (RimWorld reports 'Config error: graphicClass is null' at load)."
                )
            if not (gd.findtext("texPath") or "").strip():
                fail(
                    f"{xml_path.relative_to(ROOT)}: ThingDef '{name}' has graphicData but empty "
                    "texPath (RimWorld reports 'Config error: texPath is null or empty')."
                )


    # 8. **我们自己的** Def / CompProperties 元素必须对得上已声明字段（2026-10-06）。
    #    WHY（同族两起）：
    #      ① 大气层 [A1] 建密度数据：`<atmosphereRho>` 少打一个 0 ⇒ RimWorld 只报一行 XML error、
    #         值静默留在默认 ⇒ 密度模型静默失效；
    #      ② 2026-10-06 游戏日志实测真红字：`<defaultMode>Auto</defaultMode>` 打在
    #         `CompProperties_MapDesignator` 上 —— 该类**没有这个字段**（模式是运行时 gizmo 开关
    #         `autoFire`）。元素从落地那天起就没生效过，只贡献一行红字。
    #    范围（刻意窄，零误报优先）：只查**基类字段可枚举**的类型 ——
    #      · `Class="PioneerFramework.*CompProperties_*"`：基类 `Verse.CompProperties` 的字段只有 `compClass`；
    #      · 简单 Def 派生（`SIMPLE_DEF_TYPES` 里逐个登记）：基类 `Def` 的字段见 `DEF_BASE_FIELDS`。
    #    ⚠ 不查 ThingDef 派生（`Combat.WeaponDef` 等）：基类字段是 RimWorld 的、这里枚举不全 ⇒ 会误报。
    #    ⚠ GenStep / WorldGenStep 同样跳过（今天它们没有子元素；要查得先把基类字段登记进来）。
    DEF_BASE_FIELDS = {
        "defName", "label", "labelShort", "description",
        "ignoreConfigErrors", "modExtensions",
    }
    COMP_PROPS_BASE_FIELDS = {"compClass"}
    # ⚠ 2026-10-15（反射清理 A-5）：本库自己的 `CompProperties` 子类若继承 **vanilla 基类**，
    #   其字段来自 vanilla——本检查只扫 `Source/`（看不到 vanilla）⇒ 按"子类名 → 已知继承字段"登记。
    #   登记纪律：每条都必须对 1.7 基准核实过字段真在某个基类上（出处写在注释里），否则就是给自己开后门。
    INHERITED_FIELDS = {
        # `CompProperties_Launchable_PF : CompProperties_Launchable_TransportPod : CompProperties_Launchable`
        # ⇒ 出处：`RimWorld/CompProperties_Launchable.cs:25/28`（2026-10-15 核对）
        "CompProperties_Launchable_PF": {"activeTransporterDef", "skyfallerLeaving"},
    }
    SIMPLE_DEF_TYPES = {"CelestialBodyDef", "PropellantDef"}
    # ⚠ 2026-10-14：尾部允许**行尾注释**（`public string x = null!;  // null!：…`）。
    #   原式要求 `;\s*$` ⇒ "字段行带行尾注释"这类合法声明会被漏收（N2 可空族批加了注释才暴露）。
    #   判据不放宽：仍是"以 `;` 收尾的字段声明行"，只是允许其后跟注释。
    _field_re = re.compile(
        r"^\s*public\s+(?:readonly\s+|static\s+|volatile\s+)*[\w\.<>\[\],\?]+\s+(\w+)\s*(?:=[^;{]*)?;\s*(?://.*)?$")

    def _class_re(type_name):
        return re.compile(r"\b(?:class|struct)\s+" + re.escape(type_name) + r"\b")

    src_root = ROOT / "Source"
    declared_cache = {}

    def declared_fields(type_name):
        """声明该类型的那个 .cs 文件里，所有 `public … <名>;` 形式的字段名（宽松：同文件全收，宁松勿误报）。"""
        if type_name in declared_cache:
            return declared_cache[type_name]
        found = None
        for cs_path in sorted(src_root.rglob("*.cs")):
            parts = cs_path.parts
            if "已弃置" in parts or "bin" in parts or "obj" in parts:
                continue
            text = cs_path.read_text(encoding="utf-8", errors="replace")
            if not _class_re(type_name).search(text):
                continue
            names = set()
            for line in text.splitlines():
                m = _field_re.match(line)
                if m:
                    names.add(m.group(1))
            found = names
            break
        declared_cache[type_name] = found
        return found

    for xml_path, root in xml_roots:
        rel = xml_path.relative_to(ROOT)
        for node in root.iter():
            cls = node.get("Class")
            if cls and cls.startswith("PioneerFramework."):
                type_name = cls.split(".")[-1]
                if "CompProperties" not in type_name:
                    continue                      # GenStep 等：基类字段不可枚举 ⇒ 跳过
                base_fields = COMP_PROPS_BASE_FIELDS | INHERITED_FIELDS.get(type_name, set())
            elif node.tag.startswith("PioneerFramework."):
                type_name = node.tag.split(".")[-1]
                if type_name not in SIMPLE_DEF_TYPES:
                    continue
                base_fields = DEF_BASE_FIELDS
            else:
                continue

            declared = declared_fields(type_name)
            if declared is None:
                fail(f"{rel}: XML references type '{cls or node.tag}' but no class/struct "
                     f"'{type_name}' is declared under Source/")
            for el in node:
                if el.tag not in declared and el.tag not in base_fields:
                    fail(
                        f"{rel}: {cls or node.tag} has element <{el.tag}> which is not a declared field "
                        f"of {type_name} (RimWorld would ignore it and the value would silently stay "
                        "at its default)"
                    )

    print(
        f"OK: {len(def_names)} defs, {len(fields)} DefOf fields, "
        f"{len(xml_roots)} Def XML files, required assets present, "
        f"About.xml and optional Languages/LoadFolders checks passed."
    )


if __name__ == "__main__":
    main()
