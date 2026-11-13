"""Versioned celestial XML checks, separate from the legacy field scanner.

Only public fields belonging to the selected class and its bases are accepted.
This is a schema audit, not a C# compiler; native XML loading is also tested.
"""
import math
import re
from dataclasses import dataclass
from pathlib import Path


DEF_FIELDS = {name: "string" for name in (
    "defName", "label", "labelShort", "description", "ignoreConfigErrors", "modExtensions")}
FROZEN_BODY_FIELDS = set("""
bodyId bodyType radius gravitationalParameter hasSurface atmosphereThickness atmosphereRho0
atmosphereScaleHeight atmosphereRestoreScaleHeights atmosphereColor baseSphereColor dayLengthTicks
planetLayerDefName orbitRadius orbitPeriod orbitNormal initialPhase parentBodyDefName moonBodies
""".split()) | DEF_FIELDS.keys()
CELESTIAL_DEFS = {
    "CelestialBodyDef", "CelestialSystemDef", "CelestialBiomeProfileDef", "CelestialMapProfileDef",
    "CelestialEnvironmentProfileDef", "CelestialVisualProfileDef",
}
# 2026-11-07 A-3：枚举字段的取值白名单。枚举文本拼错时原版解析只 Log.Error 并退回默认值，
# 曾经导致「CommittedIllumination」拼错后天体静默退回 Authored；LatitudeBands 已退役同理。
CELESTIAL_ENUMS = {
    "CelestialClimateMode": {"Authored", "CommittedIllumination"},
    "CelestialBiomeGenerationMode": {"VanillaSurface", "Parameterized"},
}
MOTION_NUMERIC_KEYS = {
    "orbitRadiusMeters", "orbitPeriodSeconds", "orbitPhaseRadians",
    "orbitNormalX", "orbitNormalY", "orbitNormalZ", "rotation.axisX", "rotation.axisY",
    "rotation.axisZ", "rotation.periodSeconds", "rotation.phaseRadians",
}
_MASK = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'')
_CLASS = re.compile(r'\bclass\s+(\w+)(?:<[^>{}]*>)?\s*(?::\s*([^{}]+?))?\s*\{')
_FIELD = re.compile(r'\bpublic\s+(?:(?:static|readonly|volatile|new)\s+)*'
                    r'([\w.<>\[\],?]+)\s+(\w+)\s*(?==(?!=|>)|;)')


@dataclass(frozen=True)
class ClassSchema:
    base: str
    fields: dict


def _short(name):
    return name.split(".")[-1].split("<")[0].rstrip("?")


def build_schema_index(paths):
    """Read only supplied production source roots; tests can supply an isolated fixture."""
    if isinstance(paths, (str, Path)):
        paths = [Path(paths)]
    files = set()
    for path in paths:
        path = Path(path)
        if path.is_dir():
            files.update(source for source in path.rglob("*.cs")
                         if not any(part.casefold() in {"bin", "obj"}
                                    for part in source.relative_to(path).parts[:-1]))
        else:
            files.add(path)
    # Partial declarations can list separate interfaces; only class inheritance supplies XML fields.
    sources = []
    for path in sorted(files):
        source = path.read_text(encoding="utf-8-sig")
        source = _MASK.sub(lambda match: re.sub(r'[^\n]', ' ', match.group()), source)
        sources.append(source)
    interfaces = {name for source in sources for name in re.findall(r'\binterface\s+(\w+)', source)}
    index = {}
    for source in sources:
        for declaration in _CLASS.finditer(source):
            depth, end = 1, declaration.end()
            while end < len(source) and depth:
                depth += (source[end] == "{") - (source[end] == "}")
                end += 1
            body = source[declaration.end():end - 1]
            fields, depth, cursor = {}, 1, 0
            for field in _FIELD.finditer(body):
                prefix = body[cursor:field.start()]
                depth += prefix.count("{") - prefix.count("}")
                if depth == 1:
                    fields[field.group(2)] = field.group(1)
                cursor = field.start()
            base = (declaration.group(2) or "").split(",")[0].split("where")[0].strip()
            if _short(base) in interfaces:
                base = ""
            name = declaration.group(1)
            previous = index.get(name)
            if previous:
                if previous.base and base and previous.base != _short(base):
                    raise ValueError("Conflicting celestial schema classes: " + name)
                fields = previous.fields | fields
                base = base or previous.base
            index[name] = ClassSchema(_short(base), fields)
    return index


def _fields(name, index, seen=None):
    if name == "Def":
        return dict(DEF_FIELDS)
    seen = set() if seen is None else seen
    if name in seen or name not in index:
        return {}
    seen.add(name)
    schema = index[name]
    return _fields(schema.base, index, seen) | schema.fields


def _derives(name, ancestor, index):
    seen = set()
    while name and name not in seen:
        if name == ancestor:
            return True
        seen.add(name)
        name = index[name].base if name in index else ""
    return False


def is_celestial_node(node, index):
    full_name = node.get("Class") or node.tag
    if not full_name.startswith("PioneerFramework."):
        return False
    name = _short(full_name)
    return name in CELESTIAL_DEFS or _derives(name, "CelestialCompProperties", index)


def validate_numeric_parameter(node, integral=False):
    errors, values = [], {}
    for child in node:
        if child.tag not in {"value", "min", "max"}:
            errors.append("Unknown parameter field: " + child.tag)
            continue
        if child.tag in values:
            errors.append("Duplicate parameter field: " + child.tag)
            continue
        text = (child.text or "").strip()
        try:
            if integral:
                if not re.fullmatch(r'[+-]?[0-9]+', text):
                    raise ValueError()
                value = int(text)
                if not -2147483648 <= value <= 2147483647:
                    raise ValueError()
            else:
                value = float(text)
                if not math.isfinite(value):
                    raise ValueError()
            values[child.tag] = value
        except ValueError:
            errors.append("Invalid parameter number: " + child.tag)
    if not errors:
        if set(values) not in ({"value"}, {"min", "max"}):
            errors.append("Parameter requires value or min/max exclusively")
        elif "min" in values and values["min"] > values["max"]:
            errors.append("Parameter min exceeds max")
    return errors


def validate_celestial_definition(node, version, index):
    def visit(element, type_name, path):
        name = _short(element.get("Class") or type_name)
        if name in {"CelestialDoubleParameterXml", "CelestialIntParameterXml"}:
            return [path + ": " + error for error in validate_numeric_parameter(
                element, integral=name == "CelestialIntParameterXml")]
        if name not in index:
            return [path + ": unknown celestial XML type " + name]
        fields = _fields(name, index)
        if version == "1.6" and name == "CelestialBodyDef":
            fields = {field: "string" for field in FROZEN_BODY_FIELDS}
        errors = []
        seen = set()
        for child in element:
            child_path = path + "/" + child.tag
            if child.tag in seen:
                errors.append(child_path + ": duplicate field")
            seen.add(child.tag)
            if child.tag not in fields:
                errors.append(child_path + ": not a public field of " + name)
                continue
            field_type = fields[child.tag]
            scalar = field_type.rstrip("?")
            if scalar in {"double", "float", "int", "long", "bool"}:
                try:
                    value = (child.text or "").strip()
                    if len(child):
                        raise ValueError()
                    if scalar == "bool":
                        if value.lower() not in {"true", "false"}:
                            raise ValueError()
                    elif scalar in {"int", "long"}:
                        number = int(value)
                        bits = 32 if scalar == "int" else 64
                        if not -(2 ** (bits - 1)) <= number < 2 ** (bits - 1):
                            raise ValueError()
                    else:
                        number = float(value)
                        if not math.isfinite(number) or scalar == "float" and abs(number) > 3.4028234663852886e38:
                            raise ValueError()
                except ValueError:
                    errors.append(child_path + ": invalid " + scalar + " scalar")
            if scalar in CELESTIAL_ENUMS:
                value = (child.text or "").strip()
                if len(child) or value not in CELESTIAL_ENUMS[scalar]:
                    errors.append(child_path + ": invalid " + scalar + " value")
            if name == "CelestialMotionXml" and child.tag == "parameters":
                keys = [item.findtext("key") for item in child]
                if len(keys) != len(set(keys)) or any(key not in MOTION_NUMERIC_KEYS for key in keys):
                    errors.append(child_path + ": unknown or duplicate motion numeric key")
            collection = re.fullmatch(r'(?:List|IReadOnlyList)<(.+)>', field_type)
            if collection:
                field_type = collection.group(1)
                for item in child:
                    actual_type = _short(item.get("Class") or field_type)
                    if item.get("Class") or actual_type in index:
                        errors.extend(visit(item, actual_type, child_path + "/" + item.tag))
            elif _short(field_type) in index or _short(field_type) in {
                "CelestialDoubleParameterXml", "CelestialIntParameterXml"
            }:
                # Def references use text, not inline configuration.
                if len(child) or not _derives(_short(field_type), "Def", index):
                    # 2026-11-07 A-1：空 `<climate />`/`<exposure />` 会被原版实例化成默认对象并静默启用 v2。
                    if len(child) == 0 and _short(field_type) in index and not _derives(_short(field_type), "Def", index) \
                            and not _short(field_type).endswith("ParameterXml"):
                        errors.append(child_path + ": empty configuration element for " + _short(field_type))
                    errors.extend(visit(child, field_type, child_path))
        return errors

    return visit(node, node.get("Class") or node.tag, node.tag)
