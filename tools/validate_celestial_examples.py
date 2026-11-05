"""Validate the independent celestial sample with its own C# schema and languages."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from celestial_schema import build_schema_index, is_celestial_node, validate_celestial_definition
from celestial_localization import validate_languages


def validate_example_content(folder, schema_index, known_definitions=()):
    folder = Path(folder)
    errors = []
    definitions = set(known_definitions)
    documents = []
    for path in sorted(folder.rglob('*.xml')):
        if 'obj' in path.relative_to(folder).parts or 'bin' in path.relative_to(folder).parts:
            continue
        try:
            root = ET.parse(path).getroot()
            documents.append((path, root))
            if root.tag == 'Defs':
                definitions.update(node.findtext('defName') for node in root if node.findtext('defName'))
        except ET.ParseError as error:
            errors.append(f'{path.relative_to(folder)}: {error}')
    for path, root in documents:
        if root.tag != 'Defs':
            continue
        for node in root:
            if not is_celestial_node(node, schema_index):
                continue
            prefix = str(path.relative_to(folder)) + ': '
            errors.extend(prefix + error for error in validate_celestial_definition(node, '1.7', schema_index))
            if node.tag.split('.')[-1] != 'CelestialBodyDef':
                continue
            components = node.find('comps')
            if components is None:
                continue
            keys = set()
            for component in components:
                key = (component.findtext('key') or '').strip()
                if not key:
                    errors.append(prefix + 'MissingCompKey')
                elif key in keys:
                    errors.append(prefix + 'DuplicateCompKey: ' + key)
                keys.add(key)
                for profile in component.findall('profile'):
                    if not profile.text or profile.text.strip() not in definitions:
                        errors.append(prefix + 'MissingProfile: ' + str(profile.text))
    errors.extend(validate_languages(folder / 'Languages', definitions))
    return errors


def main():
    workspace = Path(__file__).resolve().parents[1]
    folder = workspace / 'Examples/CelestialCompSample'
    index = build_schema_index([workspace / 'Source/PioneerFramework/World/Celestial',
                                workspace / 'Source/PioneerFramework/Platform/Defs/CelestialBodyDef.cs', folder])
    known = set()
    for path in (workspace / '1.7/Defs').rglob('*.xml'):
        known.update(node.findtext('defName') for node in ET.parse(path).getroot() if node.findtext('defName'))
    errors = validate_example_content(folder, index, known)
    if errors:
        print('\n'.join(errors))
        return 1
    print('Celestial sample XML, class-local fields, component keys, profiles and zh/en translations PASS.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
