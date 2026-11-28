"""NFexp 载荷物品（DMSNF 11 款）的内容契约回归（2026-11-28）。

这些 Def 是 PFL 载荷系统（`IPayloadCarrier` / `IPayloadDeployer` / F4-2 / F4-7）的
**内容侧样本**：它们是 DMSNF 的逐字节副本，刻意保持同名同数值（靠 ModConfig 互斥
加载）。本文件钉住三件容易静默漂移的事：

  1. **数值保真**：质量 / 市价 / HP / stackLimit / texPath 与迁移时的表逐项一致。
     md5 只能证明"当初没抄错"，证明不了"之后没落后"——DMSNF 改了数值这里是看不出来的，
     所以至少要有一张可读的基线表，改的时候能看见 diff。
  2. **texPath 形态**：必须是 `Things/Resource/Payloads/<名>`（无 `Textures/` 前缀、
     无 `.png`、无前导斜杠）。后来者"顺手补全"任何一样都会白图，而闸门只查"文件存在"，
     查不出形态错。
  3. **不引 DMS / FFF**：文件里除自身 defName 外不得出现 `DMS.` / `Fortified` /
     `PioneerFramework`，也不得出现配方/研究引用。
  4. **1.7 运行时集合无重名**（按 Def 类型）：`validate_config.py` 的 `def_names` 是
     `set`，重复 defName 会被静默吞掉；这里补上它看不见的那一面。
"""
import re
import sys
import unittest
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NFEXP_DEFS = ROOT / "NFexp" / "1.7" / "Defs"
PAYLOAD_XML = NFEXP_DEFS / "Things_item" / "SpaceProject" / "DMSNF_SpaceItem.xml"
NFEXP_TEXTURES = ROOT / "NFexp" / "1.7" / "Textures"

# 迁移基线（2026-11-28 自 DMSNF 逐字节复制时的取值）。改这张表 = 有意改变内容契约。
EXPECTED = [
    # defName, Mass, MarketValue, MaxHitPoints, stackLimit, texPath
    ("DMSNF_SpaceSurveillancePayload", "350", "712", "100", "5", "Things/Resource/Payloads/SpaceSurveillancePayload"),
    ("DMSNF_DataRelayPayload", "300", "340", "100", "5", "Things/Resource/Payloads/DataRelayPayload"),
    ("DMSNF_SpaceStationModulePayload", "12000", "6000", "900", "1", "Things/Resource/Payloads/SpaceStationModulePayload"),
    ("DMSNF_BandNodeModulePayload", "2000", "4200", "200", "1", "Things/Resource/Payloads/BandNodeModulePayload"),
    ("DMSNF_SpaceSolarPowerStationModulePayload", "3000", "9855", "400", "1", "Things/Resource/Payloads/SpaceSolarPowerStationModulePayload"),
    ("DMSNF_OrbitalAssemblyModulePayload", "6000", "15000", "600", "1", "Things/Resource/Payloads/OrbitalAssemblyModulePayload"),
    ("DMSNF_UpboundCargoModulePayload", "1000", "2070", "150", "5", "Things/Resource/Payloads/UpboundCargoModulePayload"),
    ("DMSNF_BMEMP", "500", "1680", "120", "5", "Things/Resource/Payloads/BMEMP"),
    ("DMSNF_BMHE", "500", "1165", "100", "5", "Things/Resource/Payloads/BMHE"),
    ("DMSNF_BMN", "500", "2000", "150", "5", "Things/Resource/Payloads/BMN"),
    ("DMSNF_UOSF_Payload", "5000", "18000", "1500", "1", "Things/Resource/Payloads/UOSF_Payload"),
]

# 无 `Textures/` 前缀、无扩展名、无前导斜杠（多一个 `/` 都不行）。
TEXPATH_RE = re.compile(r"^Things(?:/[A-Za-z0-9_]+)+$")
FORBIDDEN = ("Fortified", "DMS.", "PioneerFramework", "DMSNF_Ceramet")


def load_payload_defs():
    return [c for c in ET.parse(PAYLOAD_XML).getroot() if c.tag == "ThingDef"]


class PayloadContentContractTests(unittest.TestCase):
    def test_eleven_defs_one_to_one_with_baseline(self):
        defs = load_payload_defs()
        self.assertEqual(11, len(defs))
        names = [d.findtext("defName").strip() for d in defs]
        self.assertEqual([e[0] for e in EXPECTED], names)
        self.assertEqual(len(names), len(set(names)), "defName 重复")

    def test_numbers_and_paths_match_baseline(self):
        by_name = {d.findtext("defName").strip(): d for d in load_payload_defs()}
        for def_name, mass, price, hp, stack, tex in EXPECTED:
            with self.subTest(def_name=def_name):
                d = by_name[def_name]
                self.assertEqual(mass, d.findtext("statBases/Mass").strip())
                self.assertEqual(price, d.findtext("statBases/MarketValue").strip())
                self.assertEqual(hp, d.findtext("statBases/MaxHitPoints").strip())
                self.assertEqual(stack, d.findtext("stackLimit").strip())
                self.assertEqual(tex, d.findtext("graphicData/texPath").strip())

    def test_all_are_resource_items_in_manufactured(self):
        for d in load_payload_defs():
            with self.subTest(def_name=d.findtext("defName")):
                self.assertEqual("ResourceBase", d.get("ParentName"))
                self.assertEqual("Graphic_Single", d.findtext("graphicData/graphicClass"))
                self.assertEqual("Manufactured", d.findtext("thingCategories/li"))

    def test_texpath_shape(self):
        for d in load_payload_defs():
            tex = d.findtext("graphicData/texPath").strip()
            with self.subTest(tex=tex):
                self.assertRegex(tex, TEXPATH_RE)
                self.assertNotIn("Textures/", tex)
                self.assertFalse(tex.endswith(".png"))

    def test_every_texpath_resolves_under_nfexp(self):
        for d in load_payload_defs():
            tex = d.findtext("graphicData/texPath").strip()
            with self.subTest(tex=tex):
                png = NFEXP_TEXTURES / f"{tex}.png"
                self.assertTrue(png.is_file(), f"缺贴图：{png}")

    def test_no_dms_or_fff_references(self):
        text = PAYLOAD_XML.read_text(encoding="utf-8")
        for token in FORBIDDEN:
            with self.subTest(token=token):
                self.assertEqual(0, text.count(token))
        # `DMSNF_` 只允许作为本文件自己的 defName 出现（不允许"引用式"出现）。
        own = {e[0] for e in EXPECTED}
        stray = set(re.findall(r"DMSNF_[A-Za-z0-9_]+", text)) - own
        self.assertEqual(set(), stray)

    def test_no_recipe_or_research_payload(self):
        """迁移只搬物品 Def；配方 / 13 个 ASP 研究 / 材料 / 试车台一律不碰。"""
        text = PAYLOAD_XML.read_text(encoding="utf-8")
        for tag in ("researchPrerequisites", "recipeUsers", "<recipes>", "<comps>"):
            with self.subTest(tag=tag):
                self.assertNotIn(tag, text)


class RuntimeLoadSetDuplicateTests(unittest.TestCase):
    """1.7 游戏里实际同时加载的集合：PFL/1.7/Defs + NFexp/1.7/Defs（按 Def 类型各一个 DefDatabase）。"""

    def test_no_duplicate_defnames_per_def_type(self):
        seen = defaultdict(list)
        for base in (ROOT / "1.7" / "Defs", NFEXP_DEFS):
            for path in base.rglob("*.xml"):
                for child in ET.parse(path).getroot():
                    name = child.findtext("defName")
                    if name:
                        seen[(child.tag, name.strip())].append(str(path.relative_to(ROOT)))
        duplicates = {k: v for k, v in seen.items() if len(v) > 1}
        self.assertEqual({}, duplicates)

    def test_payload_defnames_are_shared_with_dmsnf_by_design(self):
        """同名是刻意设计（ModConfig 互斥加载），不是笔误 —— 别被上一条测试误导去改名。"""
        names = {d.findtext("defName").strip() for d in load_payload_defs()}
        self.assertTrue(all(n.startswith("DMSNF_") for n in names))


if __name__ == "__main__":
    unittest.main()
