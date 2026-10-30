"""Language checks keep Keyed and each DefInjected type in separate namespaces."""
import re
import xml.etree.ElementTree as ET
from pathlib import Path


def validate_package_languages(packages, known_definitions=None):
    errors = []
    for package in packages:
        folder = Path(package) / 'Languages'
        if folder.exists():
            errors.extend(str(package) + ': ' + error for error in validate_languages(folder, known_definitions))
    return errors


def validate_languages(folder, known_definitions=None):
    folder = Path(folder)
    errors = []
    translations = {'English': {}, 'ChineseSimplified': {}}
    for language, entries in translations.items():
        language_root = folder / language
        if not language_root.exists():
            errors.append('MissingLanguage: ' + language)
            continue
        for path in sorted(language_root.rglob('*.xml')):
            relative = path.relative_to(language_root)
            if relative.parts[0] == 'Keyed':
                scope = 'Keyed'
            elif len(relative.parts) >= 3 and relative.parts[0] == 'DefInjected':
                scope = 'DefInjected/' + relative.parts[1]
            else:
                continue
            try:
                document = ET.parse(path).getroot()
            except ET.ParseError as error:
                errors.append(str(path) + ': ' + str(error))
                continue
            if document.tag != 'LanguageData':
                errors.append(str(path) + ': InvalidLanguageRoot')
            for node in document:
                key = scope + ':' + node.tag
                if key in entries:
                    errors.append(str(path) + ': DuplicateTranslation: ' + key)
                if node.text is None:
                    errors.append(str(path) + ': EmptyTranslation: ' + key)
                if scope.startswith('DefInjected/') and known_definitions is not None and node.tag.split('.', 1)[0] not in known_definitions:
                    errors.append(str(path) + ': MissingTranslatedDefinition: ' + node.tag)
                entries[key] = node.text or ''
    english, chinese = translations.values()
    if english.keys() != chinese.keys():
        errors.append('TranslationKeyMismatch: ' + ', '.join(sorted(english.keys() ^ chinese.keys())))
    for key in english.keys() & chinese.keys():
        placeholders = lambda value: set(re.findall(r'\{([a-zA-Z_0-9]+)(?:[^{}]*)\}', value))
        if placeholders(english[key]) != placeholders(chinese[key]):
            errors.append('TranslationPlaceholderMismatch: ' + key)
    return errors
