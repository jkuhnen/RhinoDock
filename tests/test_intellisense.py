from pathlib import Path
import sys
import tempfile
import unittest
from xml.etree import ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import probe_intellisense


class IntelliSenseConfigurationTests(unittest.TestCase):
    def test_discovery_selects_latest_valid_rhino_stub_and_cpython_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for version in ("8.9.1.0", "8.35.26251.13001"):
                package = root / "site-stubs" / f"rhino3d-{version}" / "Rhino"
                package.mkdir(parents=True)
                (package / "__init__.pyi").write_text("from Rhino import Geometry\n", encoding="utf-8")
            (root / "site-stubs" / "rhino3d-broken").mkdir()
            (root / "site-rhinopython").mkdir()
            (root / "site-interop").mkdir()

            paths = probe_intellisense.discover(root)

            self.assertEqual("rhino3d-8.35.26251.13001", paths[0].name)
            self.assertEqual(["site-rhinopython", "site-interop"], [path.name for path in paths[1:]])
            self.assertNotIn(root / "site-stubs", paths)

    def test_example_project_is_portable_and_includes_all_probes(self):
        project = ROOT / "examples" / "intellisense_test" / "RhinoIntelliSenseTest.pyproj"
        text = project.read_text(encoding="utf-8")
        self.assertNotRegex(text, r"[A-Za-z]:\\")
        self.assertNotIn("IronPython", text)
        self.assertIn(r".rhino-vs-bridge\typings", text)
        tree = ET.parse(project)
        includes = {node.attrib.get("Include") for node in tree.getroot().iter()}
        self.assertIn("intellisense_probe.py", includes)
        self.assertIn(r".rhino-vs-bridge\typings\scriptcontext.pyi", includes)

    def test_generated_configuration_contract_uses_ptvs_search_path(self):
        source = (ROOT / "src" / "RhinoVSBridge" / "IntelliSensePaths.cs").read_text(encoding="utf-8")
        self.assertIn('CreateElement("SearchPath"', source)
        self.assertIn("CompatibilityDirectory", source)
        self.assertIn("scriptcontext.pyi", source)
        self.assertIn("Rhino.RhinoDoc", source)
        self.assertNotIn("IronPython (", source)


if __name__ == "__main__":
    unittest.main()
