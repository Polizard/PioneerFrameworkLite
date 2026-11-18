import base64
import json
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "tools" / "celestial_payload.py"


def shipped_def(rel: str) -> Path:
    """2026-11-17：天体内容 Def 已迁到同仓附属包 NFexp；先找 PFL，再找 NFexp。"""
    for base in (ROOT / "1.7" / "Defs", ROOT / "NFexp" / "1.7" / "Defs"):
        candidate = base / rel
        if candidate.is_file():
            return candidate
    return ROOT / "1.7" / "Defs" / rel
VALID = (b'{\r\n  "orbitRadius": 12.5, "orbitPeriod": 8, "initialPhase": 0.25,\r\n'
         b'  "orbitNormal": [0, 1, 0], "dayLengthTicks": 10,\r\n'
         b'  "bodyType": "ExtensionCategory", "frameOriginReference": "parent"\r\n}\r\n')


class CelestialPayloadTests(unittest.TestCase):
    def run_tool(self, action, payload, *arguments):
        return subprocess.run([sys.executable, str(TOOL), action, "-", *arguments],
                              input=payload, capture_output=True, cwd=ROOT)

    def assert_rejected(self, payload, field, action="encode"):
        result = self.run_tool(action, payload)
        self.assertEqual(2, result.returncode, result.stderr.decode())
        self.assertEqual(b"", result.stdout)
        self.assertIn(field, result.stderr.decode())
        self.assertNotIn("Traceback", result.stderr.decode())

    def test_existing_model_roundtrip_preserves_exact_json_bytes(self):
        encoded = self.run_tool("encode", VALID)
        self.assertEqual(0, encoded.returncode, encoded.stderr.decode())
        self.assertEqual(base64.b64encode(VALID), encoded.stdout)
        decoded = self.run_tool("decode", encoded.stdout + b"\r\n")
        self.assertEqual(0, decoded.returncode, decoded.stderr.decode())
        self.assertEqual(VALID, decoded.stdout)

    def test_shipped_solar_system_authors_readable_motion_without_v1_payloads(self):
        # 2026-11-11：四个太阳系天体从 v1 烘焙载荷改为可读 <motion>（existing.circular.v2）。
        # 这条用例原来只对 "xml 里还有几个 payload" 做往返；改成钉住新授权形态与逐值数值，
        # 避免"零 payload ⇒ 零循环 ⇒ 假绿"。
        document = ET.parse(shipped_def("CelestialBodies/CelestialBodies_SolarSystem.xml"))
        bodies = document.getroot().findall("PioneerFramework.CelestialBodyDef")
        self.assertEqual({"Sun", "Earth", "Moon", "ZheSha"}, {body.findtext("defName") for body in bodies})
        self.assertEqual([], list(document.iter("initializationPayload")))
        expected = {
            "Sun": (0.0, 0.0, 0.0, 60000.0),
            "Earth": (20000000000.0, 3600000.0, 0.0, 60000.0),
            # Moon 自 2026-11-11 起是真潮汐锁定（synchronousTargetReference），不再 author 周期。
            "Moon": (80000000.0, 317900.0, 1.5, None),
            "ZheSha": (13333333333.333334, 1959591.794, 4.596, 60000.0),
        }
        for body in bodies:
            name = body.findtext("defName")
            motion = body.find("./comps/li[@Class='PioneerFramework.Celestial.CompProperties_ExternalPhysicsBinding']/motion")
            self.assertIsNotNone(motion, name)
            actual = tuple(float(motion.findtext(field)) for field in
                           ("orbitRadiusMeters", "orbitPeriodSeconds", "orbitPhaseRadians"))
            period = motion.findtext("rotation/periodSeconds")
            self.assertEqual(expected[name], actual + (None if period is None else float(period),), name)
        moon = next(body for body in bodies if body.findtext("defName") == "Moon")
        self.assertEqual("frameOrigin",
                         moon.findtext("./comps/li[@Class='PioneerFramework.Celestial.CompProperties_ExternalPhysicsBinding']/motion/rotation/synchronousTargetReference"))

    def test_opaque_provider_payload_is_binary_and_not_interpreted_as_json(self):
        payload = b"\x00\xffexternal provider payload\r\n"
        encoded = self.run_tool("encode", payload, "--mode", "opaque")
        self.assertEqual(0, encoded.returncode, encoded.stderr.decode())
        decoded = self.run_tool("decode", encoded.stdout, "--mode", "opaque")
        self.assertEqual(0, decoded.returncode, decoded.stderr.decode())
        self.assertEqual(payload, decoded.stdout)

    def test_only_explicit_output_path_is_written(self):
        with tempfile.TemporaryDirectory(prefix="PFL-celestial-payload-") as folder:
            source = Path(folder) / "orbit.json"
            target = Path(folder) / "orbit.base64"
            source.write_bytes(VALID)
            result = subprocess.run([sys.executable, str(TOOL), "encode", str(source),
                                     "-o", str(target)], capture_output=True, cwd=ROOT)
            self.assertEqual(0, result.returncode, result.stderr.decode())
            self.assertEqual(b"", result.stdout)
            self.assertEqual(VALID, source.read_bytes())
            self.assertEqual(base64.b64encode(VALID), target.read_bytes())
            self.assertEqual({source.name, target.name}, {path.name for path in Path(folder).iterdir()})

    def test_invalid_payload_leaves_existing_output_untouched(self):
        with tempfile.TemporaryDirectory(prefix="PFL-celestial-payload-") as folder:
            target = Path(folder) / "keep.txt"
            target.write_bytes(b"original")
            result = self.run_tool("encode", b"{}", "--output", str(target))
            self.assertEqual(2, result.returncode)
            self.assertEqual(b"original", target.read_bytes())

    def test_missing_and_unknown_fields_name_the_field(self):
        data = json.loads(VALID)
        for key in ("orbitRadius", "orbitPeriod", "initialPhase", "orbitNormal", "dayLengthTicks"):
            with self.subTest(key=key):
                incomplete = data.copy()
                del incomplete[key]
                self.assert_rejected(json.dumps(incomplete).encode(), key)
        self.assert_rejected(VALID.replace(b'"orbitRadius"', b'"typoRadius"'), "typoRadius")

    def test_duplicate_fields_are_rejected_with_their_name(self):
        self.assert_rejected(VALID.replace(b'"orbitRadius": 12.5', b'"orbitRadius": 0, "orbitRadius": 12.5'), "orbitRadius")

    def test_numeric_and_vector_errors_name_the_field(self):
        cases = (("orbitRadius", -1), ("orbitRadius", True), ("orbitRadius", "12"),
                 ("orbitRadius", float("nan")), ("orbitRadius", 10 ** 500),
                 ("orbitPeriod", -1), ("orbitPeriod", 0),
                 ("initialPhase", float("inf")), ("dayLengthTicks", 0),
                 ("orbitNormal", [0, 0, 0]), ("orbitNormal", [0, 1]),
                 ("orbitNormal", [0, True, 0]), ("orbitNormal", [1e308, 0, 0]),
                 ("bodyType", None), ("frameOriginReference", None))
        for key, value in cases:
            with self.subTest(key=key, value=str(value)[:30]):
                data = json.loads(VALID)
                data[key] = value
                self.assert_rejected(json.dumps(data).encode(), key)

    def test_zero_orbit_radius_permits_zero_period_and_optional_strings(self):
        data = json.loads(VALID)
        data.update(orbitRadius=0, orbitPeriod=0)
        del data["bodyType"]
        del data["frameOriginReference"]
        payload = json.dumps(data).encode()
        result = self.run_tool("encode", payload)
        self.assertEqual(0, result.returncode, result.stderr.decode())
        self.assertEqual(base64.b64encode(payload), result.stdout)

    def test_decode_validates_the_existing_model_fields(self):
        self.assert_rejected(base64.b64encode(b"{}"), "orbitRadius", action="decode")

    def test_malformed_json_and_utf8_are_structured_errors(self):
        for payload in (b"{", b"[]", b"\xff"):
            with self.subTest(payload=payload):
                self.assert_rejected(payload, "$:")

    def test_invalid_base64_is_rejected_before_json(self):
        self.assert_rejected(b"***", "base64", action="decode")


if __name__ == "__main__":
    unittest.main()
