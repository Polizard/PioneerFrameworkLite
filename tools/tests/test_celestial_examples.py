import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from celestial_schema import build_schema_index
from validate_celestial_examples import validate_example_content
from celestial_localization import validate_package_languages


class CelestialExampleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='PFL-celestial-example-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        source = self.root / 'types.cs'
        source.write_text('''
public class CelestialBodyDef : Def { public List<CelestialCompProperties> comps; }
public class CelestialVisualProfileDef : Def { }
public class CelestialCompProperties { public string key; }
public class CompProperties_Counter : CelestialCompProperties { public CelestialIntParameterXml initialCount; }
public class CompProperties_Visual : CelestialCompProperties { public CelestialVisualProfileDef profile; }
''', encoding='utf-8')
        self.index = build_schema_index(source)
        (self.root / 'Defs').mkdir()
        self.defs = self.root / 'Defs' / 'Sample.xml'
        self.xml = '''<Defs><PioneerFramework.Celestial.CelestialVisualProfileDef><defName>PF_Profile</defName></PioneerFramework.Celestial.CelestialVisualProfileDef>
<PioneerFramework.CelestialBodyDef><defName>PF_Test</defName><comps>
<li Class="Example.CompProperties_Counter"><key>counter</key><initialCount><min>1</min><max>9</max></initialCount></li>
<li Class="Example.CompProperties_Visual"><key>visual</key><profile>PF_Profile</profile></li>
</comps></PioneerFramework.CelestialBodyDef></Defs>'''
        self.defs.write_text(self.xml, encoding='utf-8')
        for language in ('English', 'ChineseSimplified'):
            folder = self.root / 'Languages' / language / 'Keyed'
            folder.mkdir(parents=True)
            (folder / 'Sample.xml').write_text('<LanguageData><PF_Value>Value {0}</PF_Value></LanguageData>', encoding='utf-8')

    def test_valid_foreign_component_fields_and_translations_are_checked(self):
        self.assertEqual([], validate_example_content(self.root, self.index))

    def test_unknown_field_and_duplicate_component_key_are_rejected(self):
        self.defs.write_text(self.xml.replace('<initialCount>', '<typo>1</typo><initialCount>').replace('<key>visual</key>', '<key>counter</key>'), encoding='utf-8')
        errors = validate_example_content(self.root, self.index)
        self.assertTrue(any('not a public field' in error for error in errors))
        self.assertTrue(any('DuplicateCompKey' in error for error in errors))

    def test_missing_profile_is_rejected(self):
        self.defs.write_text(self.xml.replace('<profile>PF_Profile</profile>', '<profile>Missing</profile>'), encoding='utf-8')
        self.assertTrue(any('MissingProfile' in error for error in validate_example_content(self.root, self.index)))

    def test_translation_keys_and_placeholders_must_match(self):
        path = self.root / 'Languages' / 'English' / 'Keyed' / 'Sample.xml'
        path.write_text('<LanguageData><PF_Value>Value {1}</PF_Value><Extra>extra</Extra></LanguageData>', encoding='utf-8')
        errors = validate_example_content(self.root, self.index)
        self.assertTrue(any('TranslationKeyMismatch' in error for error in errors))
        self.assertTrue(any('TranslationPlaceholderMismatch' in error for error in errors))

    def test_version_package_language_dirs_are_checked(self):
        package = self.root / '1.7'
        for language, key in [('English', 'PF_EnglishOnly'), ('ChineseSimplified', 'PF_ChineseOnly')]:
            path = package / 'Languages' / language / 'Keyed'
            path.mkdir(parents=True)
            (path / 'Keys.xml').write_text(f'<LanguageData><{key}>value</{key}></LanguageData>', encoding='utf-8')
        self.assertTrue(any('TranslationKeyMismatch' in error for error in validate_package_languages([package])))


if __name__ == '__main__':
    unittest.main()
