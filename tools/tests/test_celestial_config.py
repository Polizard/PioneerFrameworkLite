import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from celestial_schema import build_schema_index, validate_celestial_definition, validate_numeric_parameter


class CelestialSchemaTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="PFL-celestial-schema-")
        self.addCleanup(self.folder.cleanup)
        source = Path(self.folder.name) / "definitions.cs"
        source.write_text('''
namespace PioneerFramework.Celestial {
public abstract class CelestialCompProperties {
    public string key = "";
    public List<string> requires = new();
}
public sealed class CompProperties_Test : CelestialCompProperties {
    public CelestialDoubleParameterXml radiusMeters = new() { value = 30 };
    private int privateValue;
    public string Computed => "{ not a field }";
}
public sealed class Neighbor {
    public int unrelated;
}
public class CelestialBodyDef : Def {
    public string category = "Planet";
    public double radius;
    public List<CelestialCompProperties> comps = new();
}
}
''', encoding="utf-8")
        self.index = build_schema_index(Path(self.folder.name))

    def body(self, child):
        return ET.fromstring('<PioneerFramework.CelestialBodyDef><defName>PF_Test</defName>' + child + '</PioneerFramework.CelestialBodyDef>')

    def test_partial_interfaces_do_not_replace_or_conflict_with_the_class_base(self):
        path = Path(self.folder.name) / "partial.cs"
        path.write_text('''
public interface IFirst { }
public interface ISecond { }
public partial class Bridge : IFirst { public int first; }
public partial class Bridge : ISecond { public int second; }
public partial class CompProperties_Partial : CelestialCompProperties, IFirst { public int first; }
public partial class CompProperties_Partial : ISecond { public int second; }
''', encoding="utf-8")
        index = build_schema_index(Path(self.folder.name))
        self.assertEqual("", index["Bridge"].base)
        self.assertEqual({"first": "int", "second": "int"}, index["Bridge"].fields)
        self.assertEqual("CelestialCompProperties", index["CompProperties_Partial"].base)
        node = self.body('<comps><li Class="PioneerFramework.Celestial.CompProperties_Partial"><key>x</key><first>1</first><second>2</second></li></comps>')
        self.assertEqual([], validate_celestial_definition(node, "1.7", index))

    def test_conflicting_real_class_bases_still_reject(self):
        path = Path(self.folder.name) / "conflict.cs"
        path.write_text('public partial class Bad : FirstBase {} public partial class Bad : OtherBase {}', encoding="utf-8")
        with self.assertRaises(ValueError):
            build_schema_index(Path(self.folder.name))

    def test_generated_bin_and_obj_subtrees_do_not_change_the_schema(self):
        root = Path(self.folder.name)
        for folder in ("bin/Debug/net10.0", "obj/Debug/net10.0", "nested/BIN", "nested/Obj"):
            generated = root / folder
            generated.mkdir(parents=True)
            (generated / "generated.cs").write_text(
                'public class CompProperties_Test : WrongGeneratedBase { public int generatedOnly; }',
                encoding="utf-8")
        index = build_schema_index(root)
        self.assertEqual(self.index, index)
        node = self.body('<comps><li Class="PioneerFramework.Celestial.CompProperties_Test">'
                         '<key>shape</key><generatedOnly>1</generatedOnly></li></comps>')
        self.assertTrue(validate_celestial_definition(node, "1.7", index))

    def test_valid_new_definition_accepts_inherited_comp_fields(self):
        node = self.body('<category>Moon</category><comps><li Class="PioneerFramework.Celestial.CompProperties_Test">'
                         '<key>shape</key><radiusMeters><min>10</min><max>30</max></radiusMeters></li></comps>')
        self.assertEqual([], validate_celestial_definition(node, "1.7", self.index))

    def test_frozen_schema_accepts_old_fields_and_rejects_new_fields(self):
        self.assertEqual([], validate_celestial_definition(self.body('<radius>30</radius>'), "1.6", self.index))
        self.assertTrue(validate_celestial_definition(self.body('<category>Moon</category>'), "1.6", self.index))

    def test_unknown_field_is_rejected_in_both_versions(self):
        for version in ("1.6", "1.7"):
            with self.subTest(version=version):
                self.assertTrue(validate_celestial_definition(self.body('<typoRadius>30</typoRadius>'), version, self.index))

    def test_neighbor_private_and_computed_members_are_not_xml_fields(self):
        for field in ("unrelated", "privateValue", "Computed", "compClass"):
            with self.subTest(field=field):
                node = self.body('<comps><li Class="PioneerFramework.Celestial.CompProperties_Test">'
                                 '<key>shape</key><' + field + '>1</' + field + '></li></comps>')
                self.assertTrue(validate_celestial_definition(node, "1.7", self.index))

    def test_numeric_forms_are_checked_recursively(self):
        node = self.body('<comps><li Class="PioneerFramework.Celestial.CompProperties_Test"><key>shape</key>'
                         '<radiusMeters><value>30</value><max>40</max></radiusMeters></li></comps>')
        self.assertTrue(validate_celestial_definition(node, "1.7", self.index))

    def test_numeric_invalid_forms_are_rejected(self):
        for value in ("<min>2</min><max>1</max>", "<value>NaN</value>", "<value>inf</value>",
                      "<value>1</value><value>2</value>", "<value>1</value><typo>2</typo>", "<min>1</min>"):
            with self.subTest(value=value):
                self.assertTrue(validate_numeric_parameter(ET.fromstring('<parameter>' + value + '</parameter>')))

    def test_integer_range_has_exact_int32_boundaries(self):
        valid = ET.fromstring('<count><min>-2147483648</min><max>2147483647</max></count>')
        self.assertEqual([], validate_numeric_parameter(valid, integral=True))
        for value in ("2147483648", "-2147483649", "1.5"):
            self.assertTrue(validate_numeric_parameter(ET.fromstring('<count><value>' + value + '</value></count>'), integral=True))

    def test_real_motion_schema_accepts_typed_ranges_and_rejects_invalid_scalar_types(self):
        index = build_schema_index(Path(__file__).resolve().parents[2] / "Source/PioneerFramework")
        valid = '<motion><orbitRadiusMeters>100</orbitRadiusMeters><orbitPeriodSeconds>10</orbitPeriodSeconds><rotation><periodSeconds>60</periodSeconds><retrograde>true</retrograde></rotation><parameters><li><key>rotation.phaseRadians</key><value><min>0</min><max>2</max></value></li></parameters></motion>'
        def body(motion):
            return self.body('<comps><li Class="PioneerFramework.Celestial.CompProperties_ExternalPhysicsBinding"><key>motion</key>' + motion + '</li></comps>')
        self.assertEqual([], validate_celestial_definition(body(valid), "1.7", index))
        for bad in (valid.replace('<orbitPeriodSeconds>10', '<orbitPeriodSeconds>NaN'),
                    valid.replace('<retrograde>true', '<retrograde>sometimes'),
                    valid.replace('<max>2</max>', '<max>-1</max>'),
                    valid.replace('rotation.phaseRadians', 'rotation.typo')):
            self.assertTrue(validate_celestial_definition(body(bad), "1.7", index))


if __name__ == "__main__":
    unittest.main()
