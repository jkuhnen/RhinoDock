# Codex instructions: Rhino VS Bridge

Goal: build a robust Visual Studio 2026/2022 VSIX to edit Rhino 8 Python 3 scripts in Visual Studio, with PTVS IntelliSense and reliable direct execution in Rhino using the official `rhinocode` CLI. Do NOT confuse Visual Studio with Visual Studio Code.

## Architecture
- VSIX: C# VSSDK (`AsyncPackage`, .NET Framework 4.8, SDK-style project).
- Python editing: Microsoft's Python development workload (PTVS), no custom editor/parser.
- Rhino execution: Rhino 8.11+ `rhinocode` CLI; `StartScriptServer` in Rhino. No CodeListener dependency and no Rhino DLL in VS process.
- IntelliSense: Rhino 8-generated type stubs, site-rhinopython, and PTVS Search Paths. Test actual completion on a real machine; static XML tests are not a substitute.
- CPython `.py` primary; IronPython `.py2` optional; never run Rhino scripts using VS's ordinary Python interpreter.
- Do not import or copy Jingcheng Chen's original source without verifying its license/permission. This is an independent clean implementation.

## First Codex task
1. Open `RhinoVSBridge.sln` in Visual Studio 2026 with `Visual Studio extension development` and `Python development` workloads installed.
2. Restore NuGet and compile Debug x64/AnyCPU. Resolve API/compiler/VSIX packaging errors. Confirm VSIX contains package, manifest, and command table.
3. F5 experimental Visual Studio instance: check all 5 menu items under Tools.
4. In Rhino 8, run `StartScriptServer`; use Tools > Rhino: Diagnose Connection and Stubs to verify discovery.
5. Open `examples/hello_rhino.py` in a Python project; run Tools > Rhino: Run Active Python Script. Confirm exactly one point is added and output panel reports command exit status.
6. With two Rhino instances open, ensure explicit selection and no accidental execution in the other instance.
7. Test a script under `C:\Users\Test User\Projects\my test.py` for safe path handling.
8. Run Tools > Rhino: Configure Project IntelliSense, reload `.pyproj`, and verify member completion in Visual Studio for `Rhino.Geometry.Point3d`, `rhinoscriptsyntax.AddLine` and local Python modules. If not recognized, investigate PTVS .pyi interpretation and create a reproducible test.
9. Write a Windows CI build using Visual Studio 2026 runner and publish the `.vsix` as a build artifact.

## Not yet done / do not claim as supported
- Verified VSIX build/install (needs Windows + VS SDK).
- Verified PTVS IntelliSense for generated `.pyi` files on VS2026.
- Interactive Python debugging attached to Rhino; Rhino's ScriptEditor debugger is separate.
- Source-line traceback mapping into VS Error List.
- Reliable live hot reload/watching. Current implementation is save -> run via command.
- Persistent preferred Rhino instance and executable settings.
- Automatic cancellation of a currently executing Rhino script.

## Safety, quality
- Keep `ProcessStartInfo.UseShellExecute=false`, quote arguments correctly, do not construct `cmd.exe` strings.
- Use async process I/O and marshal VS APIs back onto UI thread. Never block VS UI waiting for script execution.
- Avoid overwriting users' `.pyproj`: back up before change and preserve existing search paths.
- Add tests for quoting (spaces/backslashes/quotes), missing script server, JSON schema changes, nonzero process exit, and multiple instances.
- Never execute scripts silently from untrusted projects or on file save by default.
