#!/usr/bin/env python3
"""PioneerFrameworkLite config validation.

Checks:
- XML under every version package folder that exists (1.6/ and/or 1.7/) is well-formed
- every PFDefs [DefOf] field has a matching defName in Defs
- About.xml declares a valid packageId, the NEWEST existing version folder in supportedVersions,
  and the brrainz.harmony dependency
- committed per-version Assemblies exist (plus the frozen 1.6 bundles / shader sources)
- CelestialBodyDef XML elements must be declared C# fields (atmosphere-data guard, 2026-10-06)
- per-version Languages Keyed/DefInjected consistency, duplicates and placeholders
- optional LoadFolders.xml well-formedness (when present)
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from celestial_schema import build_schema_index, is_celestial_node, validate_celestial_definition
from validate_celestial_examples import validate_example_content
from celestial_localization import validate_package_languages

ROOT = Path(__file__).resolve().parents[1]

# Version package folders we validate. 2026-10-13 tree split: 1.6 is FROZEN, 1.7 is the mainline;
# both are validated whenever present, so this script works before and after the 1.7 package lands.
VERSION_DIRS = [d for d in (ROOT / "1.6", ROOT / "1.7") if d.exists()]


def fail(msg):
    print(f"FAIL: {msg}")
    sys.exit(1)


# `Def` 基类的可枚举字段（§8 的 `SIMPLE_DEF_TYPES` 与 §8.5 的 map 配方共用；模块级以便工具测试导入）。
DEF_BASE_FIELDS = {
    "defName", "label", "labelShort", "description",
    "ignoreConfigErrors", "modExtensions",
}
MAP_GENERATION_SOURCE_DIR = "Source/PioneerFramework/MapGeneration"


def _map_short_type(name):
    return name.split(".")[-1].split("<")[0].rstrip("?")


def _map_fields(index, name, seen=None):
    """class block 精确字段集 + 基类链（`Def` 用 DEF_BASE_FIELDS）；返回 {字段名: 类型}。"""
    if name == "Def":
        return {field: "string" for field in DEF_BASE_FIELDS}
    seen = set() if seen is None else seen
    if not name or name in seen or name not in index:
        return {}
    seen.add(name)
    schema = index[name]
    merged = _map_fields(index, schema.base, seen)
    merged.update(schema.fields)
    return merged


def _map_item_type(field_type):
    match = re.fullmatch(r'(?:List|IReadOnlyList)<(.+)>', field_type)
    return _map_short_type(match.group(1) if match else field_type)


def validate_map_generation_node(node, index):
    """校验 map 配方 / 模块属性 / purposeRecipes 的元素名（P2-5，2026-11-06）。

    WHY：`LocalMapRecipeDef` 及其嵌套 `properties Class="…ModuleProperties"`、以及
    `CelestialMapProfileDef/purposeRecipes/li` 此前完全不在校验范围内 —— 元素名写错时 CI 全绿、
    游戏里静默取默认值（与 §8 的事故同族）。这里用 `celestial_schema.build_schema_index` 的
    **精确 class block** 字段集逐层判定（不是同文件宽松并集），只对能解析到本库 C# 类型的节点递归；
    Def 引用与值类型不递归，保持"零误报优先"。
    """
    errors = []

    def visit(element, type_name, path):
        resolved = element.get("Class")
        name = _map_short_type(resolved) if resolved else type_name
        if name not in index:
            errors.append(path + ": unknown map generation XML type " + name)
            return
        fields = _map_fields(index, name)
        for child in element:
            child_path = path + "/" + child.tag
            if child.tag not in fields:
                errors.append(child_path + ": not a public field of " + name)
                continue
            item = _map_item_type(fields[child.tag])
            if child.get("Class"):
                visit(child, item, child_path)
            elif child.tag == "properties" and item == "LocalMapModuleProperties":
                errors.append(child_path + ": requires a Class attribute naming the module properties type")
            elif item in index:
                for grandchild in child:
                    visit(grandchild, item, child_path + "/" + grandchild.tag)

    if node.tag.endswith(".LocalMapRecipeDef"):
        visit(node, "LocalMapRecipeDef", node.tag)
    elif node.tag.endswith(".CelestialMapProfileDef"):
        for child in node:
            if child.tag != "purposeRecipes":
                continue
            for item in child:
                visit(item, "LocalMapPurposeRecipeXml", node.tag + "/purposeRecipes/" + item.tag)
    return errors


def validate_ship_building_visuals(roots, index):
    namespace = "PioneerFramework.PFView.ShipBuildingVisuals."
    profile_type = "ShipBuildingVisualDef"
    extension_type = namespace + "ShipBuildingVisualExtension"
    profiles = set()
    extensions = []
    errors = []

    def check_fields(node, type_name, where):
        fields = _map_fields(index, type_name)
        seen = set()
        for child in node:
            if child.tag not in fields:
                errors.append(f"{where}: unknown {type_name} field {child.tag}")
            if child.tag in seen:
                errors.append(f"{where}: duplicate field {child.tag}")
            seen.add(child.tag)

    def check_list(node, item_type, where):
        if node is None or len(node) == 0:
            errors.append(f"{where}: missing {item_type} entries")
            return []
        for item in node:
            if item.tag != "li":
                errors.append(f"{where}: expected li for {item_type}")
            check_fields(item, item_type, where)
        return node

    for where, root in roots:
        for node in root.iter():
            if node.tag == namespace + profile_type:
                name = (node.findtext("defName") or "").strip()
                if not name or name in profiles:
                    errors.append(f"{where}: missing or duplicate visual Def name {name!r}")
                profiles.add(name)
                check_fields(node, profile_type, where)
                for item in check_list(node.find("nodes"), "VisualNodeXml", where):
                    provider = item.findtext("meshProvider", "PFL.Primitives.v1").strip()
                    mesh = item.findtext("mesh", "box").strip()
                    if provider == "PFL.Primitives.v1" and mesh.lower() not in ("", "box", "cylinder", "dish"):
                        errors.append(f"{where}: unknown primitive {mesh!r}")
                    if provider == "PFL.AssetBundle.v1" and mesh and not re.fullmatch(
                            r"[^\s:/\\]+\.[^\s:/\\]+::assets/[^:#\\]+(?:#[0-9]+)?", mesh, re.IGNORECASE):
                        errors.append(f"{where}: malformed AssetBundle mesh reference {mesh!r}")
            if node.get("Class") == extension_type:
                extensions.append((where, node))
    for where, extension in extensions:
        check_fields(extension, "ShipBuildingVisualExtension", where)
        mount_ids = set()
        for mount in check_list(extension.find("mounts"), "BuildingVisualMount", where):
            name = (mount.findtext("visual") or "").strip()
            if not name or name not in profiles:
                errors.append(f"{where}: unknown ship visual {name!r}")
            mount_id = mount.findtext("id", "main").strip()
            if not mount_id or mount_id in mount_ids:
                errors.append(f"{where}: empty or duplicate visual mount ID {mount_id!r}")
            mount_ids.add(mount_id)
    return errors


def package_texture_roots(xml_path, root_dir, version_dirs):
    """该 Def 文件所属包可见的贴图根，按 About.xml 的依赖方向取（§7 用）。

    依赖方向（`NFexp/About/About.xml` loadAfter PFL，反向不成立）：
      · **PFL 的 def 只查 PFL 自己的 Textures** —— 否则 PFL 可以悄悄指向只存在于
        NFexp 的图，只装 PFL 时才白图，而这个闸门反而放行；
      · **NFexp 的 def 可查 NFexp + PFL 的贴图**，但**只限 1.7**：NFexp 的
        `LoadFolders.xml` 只声明 `v1.7`（无 1.6 线），发到 1.7 游戏里时 PFL 的
        `1.6/Textures` 根本不加载 ⇒ 让它看见 1.6 的图会制造「闸门绿、运行时白图」的错判。

    `version_dirs` 显式传入（不读模块级 `VERSION_DIRS`），测试才能构造临时树。
    """
    pfl_roots = [v / "Textures" for v in version_dirs]
    if xml_path.relative_to(root_dir).parts[0] != "NFexp":
        return pfl_roots
    nfexp_roots = [root_dir / "NFexp" / "1.7" / "Textures"]
    nfexp_roots += [v / "Textures" for v in version_dirs if v.name == "1.7"]
    return nfexp_roots


def resolve_texture(tex_path, texture_roots):
    """在给定根列表里找 `<root>/<tex_path>.png`；找不到返回 None。"""
    for root in texture_roots:
        local = root / f"{tex_path}.png"
        if local.is_file():
            return local
    return None


def main():
    # 1. XML well-formed + collect defNames (all existing version package folders)
    defs_dirs = [v / "Defs" for v in VERSION_DIRS if (v / "Defs").exists()]
    # 2026-11-17：天体内容 Def（系统/天体/档案/视觉/地图配方）已迁到同仓附属包 NFexp。
    # 它的 Defs 照样过本体校验；About/Assemblies/Languages 这类包级项仍只对 VERSION_DIRS 生效。
    content_defs = ROOT / "NFexp" / "1.7" / "Defs"
    if content_defs.exists():
        defs_dirs.append(content_defs)
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

    patch_roots = []
    for patches_dir in (v / "Patches" for v in VERSION_DIRS):
        if patches_dir.exists():
            for xml_path in sorted(patches_dir.rglob("*.xml")):
                try:
                    patch_roots.append((xml_path, ET.parse(xml_path).getroot()))
                except ET.ParseError as e:
                    fail(f"{xml_path.relative_to(ROOT)}: XML parse error: {e}")

    # Validate PFView fields against their declaring C# classes, including XML patch payloads.
    visual_index = build_schema_index([ROOT / "Source/PioneerFramework/Presentation/PFView/ShipBuildingVisuals/Definitions"])
    visual_errors = validate_ship_building_visuals(
        [(path.relative_to(ROOT), root) for path, root in xml_roots + patch_roots
         if path.relative_to(ROOT).parts[0] == "1.7"], visual_index)
    if visual_errors:
        fail(visual_errors[0])

    # 2. PFDefs fields must exist in Defs
    defof_path = ROOT / "Source" / "PioneerFramework" / "Platform" / "Content" / "PFDefs.cs"
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

    # 5. Per-version languages; keys are scoped by Keyed/DefInjected type.
    language_errors = validate_package_languages(VERSION_DIRS, def_names)
    if language_errors:
        fail(language_errors[0])

    # 6. Optional LoadFolders validation
    load_folders = ROOT / "LoadFolders.xml"
    if load_folders.exists():
        try:
            ET.parse(load_folders)
        except ET.ParseError as e:
            fail(f"LoadFolders.xml: XML parse error: {e}")

    # 7. Rule 2: every graphicData/texPath must resolve to an actual texture.
    #    Base-game textures are packed in Unity assets (no loose file on disk),
    #    so valid base references are tracked via the allowlist below.
    #    2026-11-28：贴图按**包**分流（`package_texture_roots` 的 docstring 是唯一口径）。
    #    历史：`main()` 早把 `NFexp/1.7/Defs` 放进了 `xml_roots`，而这里原先只扫
    #    `VERSION_DIRS` ⇒ 任何带贴图的 NFexp 内容都被误报 missing texture。
    KNOWN_BASE_TEX = {
        "Things/Building/Misc/TempControl/Cooler",
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

    for xml_path, root in xml_roots:
        rel_xml = xml_path.relative_to(ROOT)
        tex_roots = package_texture_roots(xml_path, ROOT, VERSION_DIRS)
        searched = " + ".join(str(r.relative_to(ROOT)) for r in tex_roots)
        for child in root:
            tex = child.findtext("graphicData/texPath")
            if tex:
                tex = tex.strip()
                if resolve_texture(tex, tex_roots) is None and tex not in KNOWN_BASE_TEX:
                    fail(
                        f"{rel_xml}: def '{child.findtext('defName')}' "
                        f"references missing texture '{tex}' (not in {searched} or "
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
    # DEF_BASE_FIELDS 见模块级定义（§8.5 的 map 配方校验与之共用，避免两份字典漂移）。
    COMP_PROPS_BASE_FIELDS = {"compClass"}
    # ⚠ 2026-10-15（反射清理 A-5）：本库自己的 `CompProperties` 子类若继承 **vanilla 基类**，
    #   其字段来自 vanilla——本检查只扫 `Source/`（看不到 vanilla）⇒ 按"子类名 → 已知继承字段"登记。
    #   登记纪律：每条都必须对 1.7 基准核实过字段真在某个基类上（出处写在注释里），否则就是给自己开后门。
    INHERITED_FIELDS = {
        # `CompProperties_Launchable_PF : CompProperties_Launchable_TransportPod : CompProperties_Launchable`
        # ⇒ 出处：`RimWorld/CompProperties_Launchable.cs:25/28`（2026-10-15 核对）
        "CompProperties_Launchable_PF": {"activeTransporterDef", "skyfallerLeaving"},
    }
    SIMPLE_DEF_TYPES = {"CelestialBodyDef", "PropellantDef", "ShotDef", "WeaponDef", "LocalMapRecipeDef"}
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

    celestial_index = build_schema_index([
        ROOT / "Source/PioneerFramework/World/Celestial",
        ROOT / "Source/PioneerFramework/Platform/Defs/CelestialBodyDef.cs",
        ROOT / "Examples/CelestialCompSample",
    ])
    celestial_example = ROOT / "Examples/CelestialCompSample"
    if celestial_example.exists():
        example_errors = validate_example_content(celestial_example, celestial_index, def_names)
        if example_errors:
            fail("Examples/CelestialCompSample: " + example_errors[0])
    for xml_path, root in xml_roots:
        rel = xml_path.relative_to(ROOT)
        for node in root.iter():
            if is_celestial_node(node, celestial_index):
                celestial_errors = validate_celestial_definition(node, rel.parts[0], celestial_index)
                if celestial_errors:
                    fail(f"{rel}: {celestial_errors[0]}")
                continue
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
            if type_name.startswith("ShotCompProperties_"):
                # Custom ShotComp properties do not inherit Verse.CompProperties fields.
                # Restrict each node to its own class, rather than the file-wide field union.
                source = (ROOT / "Source/PioneerFramework/Gameplay/Combat/Shots/ShotComponents.cs").read_text(encoding="utf-8")
                block = re.search(r"class\s+" + re.escape(type_name) + r"\s*:\s*ShotCompProperties\s*\{", source)
                if block is None:
                    fail(f"{rel}: unknown built-in shot component {type_name}")
                declared = set()
                depth = 1
                for line in source[block.end():].splitlines():
                    if depth == 1 and (field := _field_re.match(line)):
                        declared.add(field.group(1))
                    depth += line.count("{") - line.count("}")
                    if depth == 0:
                        break
                base_fields = set()
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

    # 8.5 Map generation recipe / module-property / purposeRecipes 元素名（P2-5，2026-11-06）。
    #     与 §8 同样的"零误报优先"：字段集来自 `Source/PioneerFramework/MapGeneration` 的精确 class block，
    #     只对能解析到本库类型的节点递归（Def 引用/枚举/值类型不递归）。
    map_index = build_schema_index([ROOT / MAP_GENERATION_SOURCE_DIR])
    for xml_path, root in xml_roots:
        rel = xml_path.relative_to(ROOT)
        for node in root.iter():
            map_errors = validate_map_generation_node(node, map_index)
            if map_errors:
                fail(f"{rel}: {map_errors[0]}")

    print(
        f"OK: {len(def_names)} defs, {len(fields)} DefOf fields, "
        f"{len(xml_roots)} Def XML files, required assets present, "
        f"About.xml and optional Languages/LoadFolders checks passed."
    )


if __name__ == "__main__":
    main()
