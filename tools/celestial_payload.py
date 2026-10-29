"""Byte-preserving Base64 authoring for celestial physics initialization payloads.

The default validator belongs only to PFL.ExistingEphemeris / existing.circular.v1.
Other providers can use explicit opaque mode; their bytes are never interpreted.
Input/output default to stdin/stdout. Only --output writes a destination file.
"""
import argparse
import base64
import binascii
import json
import math
import sys
from pathlib import Path


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"$.{key}: duplicate JSON field")
        result[key] = value
    return result


def _number(value, field):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"$.{field}: expected a finite number")
    try:
        number = float(value)
    except OverflowError:
        raise ValueError(f"$.{field}: expected a finite double") from None
    if not math.isfinite(number):
        raise ValueError(f"$.{field}: expected a finite number")
    return number


def validate_existing_json(payload):
    """Check existing.circular.v1 fields without rewriting the supplied JSON bytes."""
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError:
        raise ValueError("$: JSON must be UTF-8") from None
    try:
        data = json.loads(text, object_pairs_hook=_unique_object)
    except json.JSONDecodeError as error:
        raise ValueError(f"$: invalid JSON at line {error.lineno}, column {error.colno}: {error.msg}") from None
    if not isinstance(data, dict):
        raise ValueError("$: expected a JSON object for existing.circular.v1")
    required = ("orbitRadius", "orbitPeriod", "initialPhase", "orbitNormal", "dayLengthTicks")
    allowed = set(required) | {"bodyType", "frameOriginReference"}
    for field in data:
        if field not in allowed:
            raise ValueError(f"$.{field}: unknown field for existing.circular.v1")
    for field in required:
        if field not in data:
            raise ValueError(f"$.{field}: missing required field")
    radius = _number(data["orbitRadius"], "orbitRadius")
    period = _number(data["orbitPeriod"], "orbitPeriod")
    _number(data["initialPhase"], "initialPhase")
    day = _number(data["dayLengthTicks"], "dayLengthTicks")
    if radius < 0:
        raise ValueError("$.orbitRadius: must be >= 0 metres")
    if period < 0 or radius > 0 and period <= 0:
        raise ValueError("$.orbitPeriod: must be >= 0 seconds and > 0 when orbitRadius > 0")
    if day <= 0:
        raise ValueError("$.dayLengthTicks: must be > 0")
    normal = data["orbitNormal"]
    if not isinstance(normal, list) or len(normal) != 3:
        raise ValueError("$.orbitNormal: expected exactly three finite numbers")
    x, y, z = (_number(value, f"orbitNormal[{index}]") for index, value in enumerate(normal))
    magnitude_squared = x * x + y * y + z * z
    if not math.isfinite(magnitude_squared) or magnitude_squared <= 0:
        raise ValueError("$.orbitNormal: squared magnitude must be finite and > 0")
    # These are provider-owned strings, including extension categories and reference keys.
    for field in ("bodyType", "frameOriginReference"):
        if field in data and not isinstance(data[field], str):
            raise ValueError(f"$.{field}: expected a string")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("encode", "decode"))
    parser.add_argument("input", nargs="?", default="-", help="input file, or - for stdin")
    parser.add_argument("-o", "--output", type=Path, help="write only this destination; otherwise use stdout")
    parser.add_argument("--mode", choices=("existing.circular.v1", "opaque"), default="existing.circular.v1",
                        help="validate the default provider JSON, or explicitly preserve arbitrary provider bytes")
    arguments = parser.parse_args(argv)
    try:
        source = sys.stdin.buffer.read() if arguments.input == "-" else Path(arguments.input).read_bytes()
        if arguments.action == "decode":
            try:
                payload = base64.b64decode(b"".join(source.split()), validate=True)
            except binascii.Error:
                raise ValueError("base64: invalid initialization payload encoding") from None
        else:
            payload = source
        if arguments.mode == "existing.circular.v1":
            validate_existing_json(payload)
        output = base64.b64encode(payload) if arguments.action == "encode" else payload
        if arguments.output is None:
            sys.stdout.buffer.write(output)
        else:
            arguments.output.write_bytes(output)
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
