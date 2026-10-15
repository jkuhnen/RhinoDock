from pathlib import Path
from xml.etree import ElementTree as ET
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class IntegrationContractTests(unittest.TestCase):
    def test_registered_commands_have_matching_vsct_ids(self):
        vsct = ROOT / "src/RhinoVSBridge/RhinoVSBridge.vsct"
        tree = ET.parse(vsct)
        root = tree.getroot()
        sym = {n.attrib["name"]: int(n.attrib["value"], 16)
               for n in root.iter() if n.tag.endswith("IDSymbol")}
        expected = {
            "RunActive": 0x0100,
            "SelectInstance": 0x0101,
            "SetupIntelliSense": 0x0102,
            "ShowDiagnostics": 0x0103,
            "ChooseExecutable": 0x0104,
        }
        for name, value in expected.items():
            self.assertEqual(value, sym[name])
        code = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        for value in expected.values():
            self.assertIn("Register(commands, 0x" + format(value, "04x"), code)

    def test_package_requests_the_compiled_command_table_resource(self):
        vsct = ROOT / "src/RhinoVSBridge/RhinoVSBridge.vsct"
        code = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        resource_name = vsct.stem + ".CTMENU"
        self.assertIn(f'[ProvideMenuResource("{resource_name}", 1)]', code)

    def test_exec_protocol_is_rhinocode_and_not_codelistener(self):
        code = (ROOT / "src/RhinoVSBridge/RhinoCli.cs").read_text()
        self.assertIn('"list --json"', code)
        self.assertIn('"--rhino "', code)
        self.assertIn('" script "', code)
        self.assertIn('UseShellExecute = false', code)
        self.assertNotIn('TcpClient', code)

    def test_project_has_no_original_extension_dependencies(self):
        proj = (ROOT / "src/RhinoVSBridge/RhinoVSBridge.csproj").read_text()
        self.assertIn('Microsoft.VisualStudio.SDK', proj)
        self.assertNotIn('Microsoft.VisualStudio.Shell.14.0', proj)
        self.assertNotIn('IronPython 2.7', proj)


if __name__ == "__main__":
    unittest.main()
