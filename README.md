# Rhino VS Bridge — Visual Studio 2026 / Rhino 8 (development preview)

Standalone **clean-room** prototype of a VSIX extension for writing Rhino 8 CPython scripts in **Visual Studio** (NOT VS Code), using Visual Studio's Python Tools for editing and IntelliSense. It runs saved scripts in an existing Rhino 8 instance by calling McNeel's official `rhinocode` CLI. Original inspiration: Jingcheng Chen's RhinoPythonForVS. None of that repository's source code was copied (its license has not been confirmed).

> **Important:** This is a development preview, not a finished release. The VSIX now builds and loads on the Windows test machine with Visual Studio 2026 and Rhino 8, but Rhino type-stub IntelliSense and several edge cases still need work. Generated `.vsix` files are build artifacts and are intentionally not committed.

## Architecture

```text
Visual Studio 2026 (PTVS Python editor)
   |   IntelliSense via Python Search Paths to Rhino 8-generated .pyi stubs
   |
   +--> Rhino VS Bridge VSIX / Tools menu
              |  save active .py / .py2
              |  rhinocode list --json
              |  select Rhino instance (pipeId)
              v
          rhinocode --rhino <id> script <absolute-file>
              |
              v
         Rhino 8 (StartScriptServer enabled)
              | CPython 3 for .py, IronPython for .py2
              +--> model, command history, Rhino runtime
```

## Prerequisites on Windows

1. **Visual Studio 2026**, with the **Visual Studio extension development** workload and **Python development** workload. The project uses VS2022-compatible VSSDK APIs where practical. Command-line builds have succeeded with both Visual Studio 2026 and Visual Studio 2022; extension loading has currently been verified only in the Visual Studio 2026 experimental instance.
2. **Rhino 8.11 or newer**, with `rhinocode.exe` in `%ProgramFiles%\Rhino 8\System\`.
3. Open Rhino and enter the command **`StartScriptServer`**. Open **`ScriptEditor`** once to generate Python type stubs.
4. For editing and autocomplete, create or open a **Visual Studio Python project** (`.pyproj`) and choose a regular compatible CPython environment for its static analysis. Execution is **always** performed by Rhino, NOT by Visual Studio's selected Python interpreter.

## Compile first developer build

- Open `RhinoVSBridge.sln` in Visual Studio 2026.
- Restore NuGet packages. Build **Debug / Any CPU**. The SDK-style project uses `Microsoft.VisualStudio.SDK` 17.14 and `Microsoft.VSSDK.BuildTools` 18.5; package versions may be updated as needed.
- Use **F5** to launch the Visual Studio experimental instance, or install the generated `.vsix` after successful compilation.
- Under **Tools**, find five `Rhino:` menu commands.

The current verified build output is:

```text
src\RhinoVSBridge\bin\Debug\net48\RhinoVSBridge.vsix
```

`bin` and `obj` directories, generated VSIX packages, IDE state, test caches, and user-specific backup files are excluded from Git.

### Workflow

1. In the Rhino 8 window, execute `StartScriptServer`.
2. In Visual Studio open `examples/hello_rhino.py` or your own saved `.py` script.
3. Choose **Tools > Rhino: Diagnose Connection and Stubs** to check RhinoCode and discovered stub paths.
4. Choose **Tools > Rhino: Configure Project IntelliSense**. The command locates Rhino-generated `.pyi` stubs and Python module paths, safely backs up your existing `.pyproj` as `.rhino-bridge.bak`, and appends Search Paths. **Reload the Python project** afterward (Solution Explorer > Reload Project). If no `.pyproj` is selected, paths are copied to the clipboard instead.
5. Choose **Tools > Rhino: Run Active Python Script**. Unsaved edits in the active document are saved automatically. Output is shown in **Output > Rhino VS Bridge**. If more than one Rhino instance is found, choose the target.
6. Configure an editor shortcut for the `Rhino: Run Active Python Script` command under **Tools > Options > Environment > Keyboard** (command registration/name should be verified in experimental VS).

### IntelliSense reality check

- The `.pyi` stubs for the RhinoCommon/.NET assemblies normally appear under `~/.rhinocode/py39-rh8/site-stubs/rhino3d-<build>` once Rhino's ScriptEditor has been opened.
- `rhinoscriptsyntax` and `scriptcontext` require the actual Python library source in Rhino's `site-rhinopython` or older settings/lib folders. The extension discovers likely paths rather than downloading unsupported type packages.
- On the test machine, the extension discovers the current Rhino 8.35 stub tree and writes the expected paths to the Python project. Visual Studio 2026 starts its bundled Pylance server and local-project navigation works, but member completion from the Rhino-generated stubs has **not** been confirmed.
- `Rhino.Geometry.Point3d` is present in Rhino's generated `.pyi` files and `rhinoscriptsyntax.AddLine` is present in `site-rhinopython`; the remaining problem appears to be PTVS/Pylance search-path or stub interpretation rather than missing Rhino files.
- This extension configures project search paths; it does **not** install a custom editor or language server, copy stubs into the project, or modify Rhino's Python installation.
- Do not use Visual Studio's ordinary Python Run/Debug command for Rhino scripts. The selected interpreter is for editing and static analysis only; execute scripts through **Tools > Rhino: Run Active Python Script** so they run inside Rhino.

## Simple CLI test without any VSIX

To verify that the basic Rhino connection works *before building* the extension, install ordinary Python 3 locally and run:

```powershell
python tools\probe_rhinocode.py
python tools\probe_rhinocode.py --run examples\hello_rhino.py
```

The probe lists running Rhino instances and, with `--run`, tells Rhino to execute a file. This uses no CodeListener. For a custom Rhino install use `--executable "C:\Path\To\rhinocode.exe"`.

Run portable tests:

```powershell
python -m unittest discover -s tests -v
```

## Verified status (2026-10-15)

### Automated checks

- NuGet restore and `Debug | Any CPU` rebuild with Visual Studio Community 2026 18.11: **passed**, 0 warnings and 0 errors.
- Command-line build with Visual Studio 2022: **passed**. Loading the VSIX in Visual Studio 2022 is not yet verified.
- VSIX packaging: **passed**. The package contains `extension.vsixmanifest`, `RhinoVSBridge.dll`, and `RhinoVSBridge.pkgdef`; the compiled command table is embedded in the package assembly.
- Unit tests: **8/8 passed** with `python -m unittest discover -s tests -v`.
- Official RhinoCode discovery: `rhinocode list --json` found the running Rhino 8.35 instance and its script-server pipe.
- `examples/hello_rhino.py` executed through the official RhinoCode CLI in Rhino CPython 3, and the integration verifier confirmed exactly one point was added.
- CLI quoting with a script path containing spaces is covered by an automated test and passed.

### Actual GUI checks

- The VSIX loaded in a Visual Studio 2026 experimental instance.
- All five `Rhino:` commands were present under **Tools**.
- The package initialized successfully when its commands were invoked.
- The IntelliSense test project opened and Visual Studio's bundled Pylance 2026.3.1 started.

### Still to test or fix

- Confirm member completion in Visual Studio for `Rhino.Geometry.Point3d`, `rhinoscriptsyntax.AddLine`, and `scriptcontext`. The generated files exist, but Pylance did not reliably offer their members through the configured `.pyproj` search paths.
- Re-test the complete **Tools > Rhino: Run Active Python Script** path after the IntelliSense work. Direct execution through the same official RhinoCode protocol is already verified.
- Verify explicit target selection with two simultaneously running Rhino instances and ensure a script cannot be sent to the wrong instance.
- Perform the requested real filesystem test under `C:\Users\Test User\Projects\my test.py`; only an equivalent space-containing path and the quoting unit test have been exercised so far.
- Install and load the generated VSIX in Visual Studio 2022.
- Add Windows CI that builds and publishes the VSIX artifact.

## Current milestones

- [x] Investigated RhinoCode CLI and VS SDK, documented clean-room architecture.
- [x] Wrote initial VSIX project with Tools menu commands and asynchronous CLI execution.
- [x] Added instance picker, output window, and Python stub path discovery with `.pyproj` backup.
- [x] Added stand-alone RhinoCode connectivity probe and initial XML/CLI tests.
- [x] Restored and built the VSIX with Visual Studio 2026 on Windows without warnings or errors.
- [x] Confirmed the VSIX loads and all five commands register in the Visual Studio 2026 experimental instance.
- [x] Confirmed official RhinoCode discovery and CPython script execution in an actual running Rhino 8 instance.
- [ ] Confirm `Rhino.Geometry` and `rhinoscriptsyntax` member IntelliSense in PTVS; improve if necessary.
- [ ] Add reliable cancellation, keyboard shortcut defaults, traceback navigation, more tests, configuration persistence.
- [ ] Add Windows CI producing an installable `.vsix` artifact.

## References

- RhinoCode CLI: https://developer.rhino3d.com/en/guides/scripting/advanced-cli/
- Rhino 8 Python 3: https://developer.rhino3d.com/guides/rhinopython/python-3-in-rhino/
- VS 2026 SDK-style VSIX: https://devblogs.microsoft.com/visualstudio/sdk-style-support-for-extension-projects/
- VS 2022/2026 compatibility: https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/extension-compatibility
- Visual Studio PTVS Search Paths: https://learn.microsoft.com/en-us/visualstudio/python/search-paths
- Original extension (reference only): https://github.com/jingcheng-chen/RhinoPythonForVS

## Notes for Codex

Read `AGENTS.md`. Keep subsequent work on feature branches. The next priority is the reproducible PTVS/Pylance Rhino-stub investigation, followed by the remaining multi-instance, spaced-user-path, Visual Studio 2022 load, and CI checks. Do not add larger features until these integration basics are reliable.
