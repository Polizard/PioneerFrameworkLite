import sys
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from celestial_schema import build_schema_index
from validate_config import validate_ship_building_visuals

ROOT = Path(__file__).resolve().parents[2]
NS = "PioneerFramework.PFView.ShipBuildingVisuals."
INDEX = build_schema_index([ROOT / "Source/PioneerFramework/Presentation/PFView/ShipBuildingVisuals/Definitions"])


def fixture(node, mount):
    defs = ET.fromstring(f"<Defs><{NS}ShipBuildingVisualDef><defName>TestVisual</defName>"
                         f"<nodes><li>{node}</li></nodes></{NS}ShipBuildingVisualDef></Defs>")
    patch = ET.fromstring(f'<Patch><Operation><value><li Class="{NS}ShipBuildingVisualExtension">'
                          f'<mounts><li>{mount}</li></mounts></li></value></Operation></Patch>')
    return [("defs", defs), ("patch", patch)]


class ShipBuildingVisualConfigTests(unittest.TestCase):
    def test_shipped_visuals_and_bindings(self):
        paths = ("1.7/Defs/PFView/ShipBuildingVisuals.xml", "1.7/Patches/PFViewShipBuildingVisuals.xml")
        self.assertEqual([], validate_ship_building_visuals(
            [(path, ET.parse(ROOT / path).getroot()) for path in paths], INDEX))

    def test_misspelled_fields_and_invalid_mesh_are_reported(self):
        roots = fixture("<id>head</id><bindYa>true</bindYa><mesh>sphere</mesh>",
                        "<visual>TestVisual</visual><surfase>Top</surfase>")
        errors = "\n".join(validate_ship_building_visuals(roots, INDEX))
        for field in ("bindYa", "sphere", "surfase"):
            self.assertIn(field, errors)

    def test_reference_must_point_to_a_visual(self):
        roots = fixture("<id>head</id>", "<visual>MissingVisual</visual>")
        self.assertIn("MissingVisual", "\n".join(validate_ship_building_visuals(roots, INDEX)))

    def test_external_provider_identity_is_allowed(self):
        roots = fixture("<id>head</id><meshProvider>Example.Asset.v1</meshProvider><mesh>antenna</mesh>",
                        "<visual>TestVisual</visual><stateProvider>Example.State.v1</stateProvider>")
        self.assertEqual([], validate_ship_building_visuals(roots, INDEX))
