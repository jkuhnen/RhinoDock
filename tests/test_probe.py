import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
import sys
from xml.etree import ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import probe_rhinocode

ROOT = Path(__file__).resolve().parents[1]


class RhinoCliProbeTests(unittest.TestCase):
    def make_fake_cli(self, folder):
        folder = Path(folder)
        script = folder / "fake_rhinocode.py"
        script.write_text("#!/usr/bin/env python3\n"
                       "import json,sys\n"
                       "if sys.argv[1:] == ['list', '--json']:\n"
                       " print(json.dumps([{'pipeId': 'pipe xyz', 'processId': 1234, 'activeDoc': {'title': 'Hello.3dm'}}]))\n"
                       "elif sys.argv[1:4] == ['--rhino', 'pipe xyz', 'script']:\n"
                       " print('OK:'+sys.argv[4])\n"
                       "else: sys.exit(17)\n")
        if os.name == "nt":
            exe = folder / "fake-rhinocode.cmd"
            exe.write_text('@"{}" "%~dp0fake_rhinocode.py" %*\n'.format(sys.executable))
            return str(exe)
        exe = folder / "fake-rhinocode"
        script.rename(exe)
        exe.chmod(exe.stat().st_mode | stat.S_IXUSR)
        return str(exe)

    def test_instance_parsing_and_run_with_spaces(self):
        with tempfile.TemporaryDirectory(prefix="rhino bridge ") as tmp:
            cli = self.make_fake_cli(tmp)
            instances = probe_rhinocode.list_instances(cli)
            self.assertEqual(instances[0]["pipeId"], "pipe xyz")
            script = Path(tmp) / "my script.py"
            script.write_text("print('hello')\n")
            self.assertEqual(0, probe_rhinocode.main(["--executable", cli, "--run", str(script)]))

    def test_missing_executable(self):
        self.assertEqual(1, probe_rhinocode.main(["--executable", "/nonexistent/rhinocode"]))

    def test_project_xml_is_well_formed(self):
        for path in ROOT.rglob("*.vsct"):
            ET.parse(path)
        for path in ROOT.rglob("*.csproj"):
            ET.parse(path)
        for path in ROOT.rglob("*.vsixmanifest"):
            ET.parse(path)

    def test_vsix_targets_2022_or_later(self):
        p = ROOT / "src/RhinoVSBridge/source.extension.vsixmanifest"
        n = ET.parse(p)
        targets = n.findall(".//{*}InstallationTarget")
        self.assertEqual("[17.0,)", targets[0].attrib["Version"])
        self.assertEqual("amd64", targets[0].find("{*}ProductArchitecture").text)


if __name__ == "__main__":
    unittest.main()
