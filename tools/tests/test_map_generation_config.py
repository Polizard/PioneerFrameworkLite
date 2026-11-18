"""P2-5（2026-11-06）：map 配方 / 模块属性 / purposeRecipes 的 XML 元素名校验回归。

背景：`LocalMapRecipeDef` 及其嵌套 `properties Class="…ModuleProperties"`、以及
`CelestialMapProfileDef/purposeRecipes/li` 此前不在 `tools/validate_config.py` 的校验范围内 ——
元素名写错时 CI 全绿、游戏里静默取默认值。本文件钉住：
  1. 实际发货的 1.7 Def 内容零误报；
  2. 各类错误元素名（step / properties / 嵌套 selector / port / purposeRecipes）会被拒绝；
  3. 合法 XML（含嵌套结构）仍通过。

夹具全部在内存构造，不落临时文件（Windows 受限环境下 unittest 的 tempfile 会被 ACL 拒绝）。
"""
import sys
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from celestial_schema import build_schema_index
from validate_config import validate_map_generation_node

ROOT = Path(__file__).resolve().parents[2]
INDEX = build_schema_index([ROOT / "Source/PioneerFramework/MapGeneration"])


def shipped_path(rel: str) -> Path:
    """2026-11-17：天体/地图配方等内容 Def 已迁到同仓附属包 NFexp；先找 PFL，再找 NFexp。"""
    for base in (ROOT / "1.7" / "Defs", ROOT / "NFexp" / "1.7" / "Defs"):
        candidate = base / rel
        if candidate.is_file():
            return candidate
    return ROOT / "1.7" / "Defs" / rel

MAP_NS = "PioneerFramework.MapGeneration."
PROPERTIES = 'Class="PioneerFramework.MapGeneration.NativeRulesModuleProperties"'


def recipe(body):
    return ET.fromstring(
        f"<{MAP_NS}LocalMapRecipeDef><defName>PF_Test</defName>{body}</{MAP_NS}LocalMapRecipeDef>")


def profile(body):
    return ET.fromstring(
        "<PioneerFramework.Celestial.CelestialMapProfileDef><defName>PF_TestMaps</defName>"
        f"{body}</PioneerFramework.Celestial.CelestialMapProfileDef>")


class MapGenerationConfigTests(unittest.TestCase):
    def test_shipped_recipes_and_map_profiles_have_no_schema_errors(self):
        shipped = [
            shipped_path("CelestialFramework/LocalMapRecipes.xml"),
            shipped_path("CelestialFramework/CelestialProfiles.xml"),
            shipped_path("CelestialFramework/CelestialPlanetKindProfiles.xml"),
        ]
        checked = 0
        for path in shipped:
            self.assertTrue(path.is_file(), str(path))
            for node in ET.parse(path).getroot().iter():
                if node.tag.endswith(".LocalMapRecipeDef") or node.tag.endswith(".CelestialMapProfileDef"):
                    checked += 1
                    self.assertEqual([], validate_map_generation_node(node, INDEX), str(path))
        self.assertGreaterEqual(checked, 12)

    def test_valid_step_and_module_properties_are_accepted(self):
        node = recipe(
            "<recipeId>golden</recipeId><steps><li>"
            "<key>nativeRules</key><handlerId>PFL.LocalMap.NativeRules</handlerId>"
            "<configVersion>1</configVersion><algorithmVersion>1</algorithmVersion>"
            "<stage>Fields</stage><domain>Natural</domain><before/><after/>"
            "<inputs><li><name>in</name><key>resource.in</key><typeId>Example.Type</typeId></li></inputs>"
            "<outputs/>"
            f"<properties {PROPERTIES}><skip><li><optionalNames><li>Snow</li></optionalNames>"
            "<prefixes/><types/><biome>PF_TestBiome</biome></li></skip></properties>"
            "</li></steps>")
        self.assertEqual([], validate_map_generation_node(node, INDEX))

    def test_valid_purpose_recipe_is_accepted(self):
        node = profile("<localRecipe>PF_LocalMapNative</localRecipe><purposeRecipes>"
                       "<li><purpose>Landing</purpose><recipe>PF_LocalMapNative</recipe></li>"
                       "</purposeRecipes>")
        self.assertEqual([], validate_map_generation_node(node, INDEX))

    def test_unknown_step_field_is_rejected(self):
        node = recipe("<steps><li><handler>PFL.LocalMap.NativeRules</handler></li></steps>")
        errors = validate_map_generation_node(node, INDEX)
        self.assertTrue(any("not a public field of LocalMapGenerationStepXml" in error for error in errors), errors)

    def test_unknown_module_properties_field_is_rejected(self):
        node = recipe(
            "<steps><li><key>k</key>"
            f"<properties {PROPERTIES}><skipz/></properties></li></steps>")
        errors = validate_map_generation_node(node, INDEX)
        self.assertTrue(any("not a public field of NativeRulesModuleProperties" in error for error in errors), errors)

    def test_missing_module_properties_class_is_rejected(self):
        node = recipe("<steps><li><properties><skip/></properties></li></steps>")
        errors = validate_map_generation_node(node, INDEX)
        self.assertTrue(any("requires a Class attribute" in error for error in errors), errors)

    def test_unknown_purpose_recipe_field_is_rejected(self):
        node = profile("<purposeRecipes><li><recipez>PF_LocalMapNative</recipez></li></purposeRecipes>")
        errors = validate_map_generation_node(node, INDEX)
        self.assertTrue(any("not a public field of LocalMapPurposeRecipeXml" in error for error in errors), errors)

    def test_nested_selector_and_port_fields_are_checked(self):
        selector = recipe(
            "<steps><li><key>k</key>"
            f"<properties {PROPERTIES}><skip><li><optionalNamess/></li></skip></properties>"
            "</li></steps>")
        self.assertTrue(any("not a public field of NativeStepSelectorXml" in error
                            for error in validate_map_generation_node(selector, INDEX)))
        port = recipe("<steps><li><inputs><li><nam>n</nam></li></inputs></li></steps>")
        self.assertTrue(any("not a public field of LocalMapPortXml" in error
                            for error in validate_map_generation_node(port, INDEX)))

    def test_unknown_module_properties_class_is_rejected(self):
        node = recipe(
            "<steps><li><key>k</key>"
            '<properties Class="PioneerFramework.MapGeneration.DoesNotExist"><x/></properties>'
            "</li></steps>")
        errors = validate_map_generation_node(node, INDEX)
        self.assertTrue(any("unknown map generation XML type DoesNotExist" in error for error in errors), errors)


if __name__ == "__main__":
    unittest.main()
