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
            "CancelActive": 0x0105,
        }
        for name, value in expected.items():
            self.assertEqual(value, sym[name])
        code = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        for value in expected.values():
            self.assertIn("Register(commands, 0x" + format(value, "04x"), code)
    def test_run_command_has_shift_f2_text_editor_binding(self):

        tree = ET.parse(ROOT / "src/RhinoVSBridge/RhinoVSBridge.vsct")
        root = tree.getroot()
        bindings = [node for node in root.iter() if node.tag.endswith("KeyBinding")]
        run_binding = next(node for node in bindings
                           if node.attrib.get("id") == "RunActive")
        self.assertEqual("GUID_TextEditorFactory", run_binding.attrib["editor"])
        self.assertEqual("VK_F2", run_binding.attrib["key1"])
        self.assertEqual("Shift", run_binding.attrib["mod1"])

        command_names = [node.text for node in root.iter()
                         if node.tag.endswith("CommandName")]
        self.assertIn("RhinoVSBridge.RunActivePythonScript", command_names)

    def test_cpython_runner_captures_output_and_original_tracebacks(self):
        code = (ROOT / "src/RhinoVSBridge/RhinoExecutionRunner.cs").read_text()
        self.assertIn("contextlib.redirect_stdout", code)
        self.assertIn("contextlib.redirect_stderr", code)
        self.assertIn("compile(source_file.read(), script_path, 'exec')", code)
        self.assertIn("traceback.print_exc()", code)
        self.assertIn("sys.path.insert(0, script_directory)", code)
        self.assertIn("Convert.ToBase64String", code)
        self.assertIn("sys.settrace(cancellation_trace)", code)
        self.assertIn("os.replace(temporary_startup, startup_path)", code)
        self.assertIn("'sessionId': session_id", code)
        self.assertIn("'pipeId': pipe_id", code)
        self.assertIn("StartupTimedOut = true", code)
        self.assertIn("os.replace(temporary_result, result_path)", code)

    def test_startup_timeout_is_separate_and_dispatch_failures_release_state(self):
        cli = (ROOT / "src/RhinoVSBridge/RhinoCli.cs").read_text()
        package = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        runner = (ROOT / "src/RhinoVSBridge/RhinoExecutionRunner.cs").read_text()

        self.assertIn("ScriptStartupTimeout", cli)
        self.assertIn("TimeSpan.FromSeconds(15)", cli)
        self.assertIn("DispatchFailed", runner)
        self.assertIn("safeToRetry", runner)
        self.assertIn("if (result.DispatchFailed)", package)
        self.assertIn("executionFinalized = true", package)
        self.assertIn("if (result.StartupTimedOut)", package)

    def test_uncertain_execution_reconciliation_requires_terminal_identity(self):
        coordinator = (ROOT / "src/RhinoVSBridge/RhinoExecutionCoordinator.cs").read_text()
        package = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        runner = (ROOT / "src/RhinoVSBridge/RhinoExecutionRunner.cs").read_text()

        self.assertIn("expectedSessionId", runner)
        self.assertIn("expectedPipeId", runner)
        self.assertIn("CompletionArtifactStatus.IdentityMismatch", runner)
        self.assertIn("MonitorUncertainAsync", coordinator)
        self.assertIn("CancellationRequestStatus.AlreadyCompleted", coordinator)
        self.assertIn("ReconciliationMonitorDuration", package)
        self.assertIn("Execution slot released and ready for another run", package)
        self.assertNotIn("execution.MarkUncertain(null)", package)

    def test_python_failures_use_a_package_owned_error_list_provider(self):
        package = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        provider = (ROOT / "src/RhinoVSBridge/RhinoErrorList.cs").read_text()
        parser = (ROOT / "src/RhinoVSBridge/PythonTracebackParser.cs").read_text()

        self.assertIn("new RhinoErrorList(this)", package)
        self.assertIn("PublishPythonErrorsAsync(result.StandardError)", package)
        self.assertIn("_errors?.Clear()", package)
        self.assertIn("new ErrorListProvider(serviceProvider)", provider)
        self.assertIn('ProviderName = "Rhino Dock"', provider)
        self.assertIn("_provider.Navigate(navigationTask, VSConstants.LOGVIEWID_Code)", provider)
        self.assertIn("Document = task.Document", provider)
        self.assertIn("ToTaskProviderLine(task.Line)", provider)
        self.assertIn('string.Equals(fileName, "bridge_runner.py"', parser)
        self.assertIn("pythonLineNumber - 1", parser)

    def test_vsix_and_product_registration_are_version_054(self):
        manifest = ET.parse(
            ROOT / "src/RhinoVSBridge/source.extension.vsixmanifest").getroot()
        identity = next(node for node in manifest.iter()
                        if node.tag.endswith("Identity"))
        self.assertEqual("0.5.5", identity.attrib["Version"])
        self.assertEqual("Johannes Kuhnen", identity.attrib["Publisher"])
        self.assertEqual("RhinoVSBridge.08e0a5d2-a57b-48e5-8bf6-08f4efb3cf6b", identity.attrib["Id"])
        display = next(node for node in manifest.iter()
                       if node.tag.endswith("DisplayName"))
        self.assertEqual("Rhino Dock", display.text)
        package = (ROOT / "src/RhinoVSBridge/BridgePackage.cs").read_text()
        self.assertIn('"0.5.5"', package)

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
