# Rhino VS Bridge — Visual Studio 2026 / Rhino 8 (development preview)

Standalone **clean-room** prototype of a VSIX extension for writing Rhino 8 CPython scripts in **Visual Studio** (NOT VS Code), using Visual Studio's Python Tools for editing and IntelliSense. It runs saved scripts in an existing Rhino 8 instance by calling McNeel's official `rhinocode` CLI. Original inspiration: Jingcheng Chen's RhinoPythonForVS. None of that repository's source code was copied (its license has not been confirmed).

> **Important:** This is a development preview, not a finished release. The VSIX builds and loads on the Windows test machine with Visual Studio 2026 and Rhino 8. Full Rhino IntelliSense and the save → Shift+F2 → execute-in-Rhino workflow are verified; the remaining limitations are documented below. Generated `.vsix` files are build artifacts and are intentionally not committed.

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
          rhinocode --rhino <id> script <managed-execution-runner>
              |
              v
         Rhino 8 (StartScriptServer enabled)
              | CPython 3 for .py, IronPython for .py2
              +--> original .py, stdout/stderr, traceback and result
              +--> Visual Studio Error List tasks for Python traceback frames
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
- Under **Tools**, find six `Rhino:` menu commands.

The current verified build output is:

```text
src\RhinoVSBridge\bin\Debug\net48\RhinoVSBridge.vsix
```

`bin` and `obj` directories, generated VSIX packages, IDE state, test caches, and user-specific backup files are excluded from Git.

### Workflow

1. In the Rhino 8 window, execute `StartScriptServer`.
2. In Visual Studio open `examples/hello_rhino.py` or your own saved `.py` script.
3. Choose **Tools > Rhino: Diagnose Connection and Stubs** to check RhinoCode and discovered stub paths.
4. Choose **Tools > Rhino: Configure Project IntelliSense**. The command locates Rhino-generated `.pyi` stubs and CPython module paths, safely backs up the existing `.pyproj`, appends PTVS Search Paths, and creates a project-local `scriptcontext.pyi` shim. **Close and reopen the solution** afterward; VS 18.11 did not reliably update the already running Pylance service after Project Reload alone. If no `.pyproj` is selected, paths are copied to the clipboard instead.
5. Choose **Tools > Rhino: Run Active Python Script**. Unsaved edits in the active document are saved automatically. Output is shown in **Output > Rhino VS Bridge**. If more than one Rhino instance is found, choose the target.
6. Press **Shift+F2** in the text editor to save and run the active script. The default command binding is `Tools.RhinoRunActivePythonScript` in the **Text Editor** scope. If an existing extension or personal setting already owns Shift+F2, reassign that command under **Tools > Options > Environment > Keyboard**.
7. For a running CPython script, choose **Tools > Rhino: Cancel Running Script**. Cancellation is cooperative and is acknowledged only after execution inside Rhino reaches a Python trace point.
8. When Rhino reports a Python exception, open **View > Error List**. Entries whose descriptions begin **Rhino VS Bridge:** navigate to the reported Python frame when double-clicked, including files outside the current solution.

### IntelliSense reality check

- The `.pyi` stubs for the RhinoCommon/.NET assemblies normally appear under `~/.rhinocode/py39-rh8/site-stubs/rhino3d-<build>` once Rhino's ScriptEditor has been opened.
- PTVS passes `.pyproj` Search Paths to Pylance as `python.analysis.extraPaths`. Tools > Options > Python > Analysis > **Search paths** is an additional global `extraPaths` list; **Stubs path** is one global `stubPath` and is not appropriate for Rhino's mixed versioned-stub and Python-source roots.
- The bridge adds only the versioned root that directly contains `Rhino`, plus `site-rhinopython` and `site-interop`. It no longer adds the non-importable `site-stubs` parent or legacy IronPython `settings\lib`.
- Pylance resolves `rhinoscriptsyntax` from Rhino's source. Because `scriptcontext.py` replaces itself dynamically in `sys.modules`, the bridge creates a small project-local typing shim for its public members.
- Actual VS 2026 checks confirmed completion for `Rhino.Geometry.Point3d`, `Point3d.Origin`, `rhinoscriptsyntax.AddLine`, `scriptcontext.doc`, and a local module; Point3d signature help, QuickInfo documentation, and Go to Definition into Rhino's generated `.pyi` also worked.
- This extension does **not** install a custom editor or language server, or modify Rhino's Python installation. See [the VS 2026 investigation](docs/intellisense-vs2026.md) for the exact path handoff and genuinely observed features.
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
dotnet run --project tests\RhinoVSBridge.CoreTests
python tools\probe_intellisense.py
```

## Verified status (2026-11-03)

### Automated checks

- NuGet restore and `Debug | Any CPU` rebuild with Visual Studio Community 2026 18.11: **passed**, 0 warnings and 0 errors.
- Command-line build with Visual Studio 2022: **passed**. Loading the VSIX in Visual Studio 2022 is not yet verified.
- VSIX packaging: **passed**. The package contains `extension.vsixmanifest`, `RhinoVSBridge.dll`, and `RhinoVSBridge.pkgdef`; the compiled command table is embedded in the package assembly.
- Portable Python tests: **17/17 passed** with `python -m unittest discover -s tests -v`.
- Product C# tests: **31/31 passed**. They link the actual execution, target-selection, workspace, coordination, traceback parsing, and IntelliSense path-management sources.
- Release CI: **configured** on GitHub's Visual Studio 2026 Windows image and uploads the Release VSIX as a workflow artifact.
- Default shortcut contract: Visual Studio exposed `Tools.RhinoRunActivePythonScript` as `Text Editor::Shift+F2`.
- Rhino execution integration: the product C# implementation captured Unicode output and an intentional traceback with the original file and line number.
- IntelliSense path reconciliation removes obsolete bridge-managed Rhino version paths while preserving custom project paths.
- Official RhinoCode discovery: `rhinocode list --json` found the running Rhino 8.35 instance and its script-server pipe.
- `examples/hello_rhino.py` executed through the official RhinoCode CLI in Rhino CPython 3, and the integration verifier confirmed exactly one point was added.
- CLI quoting with a script path containing spaces is covered by an automated test and passed.
- IntelliSense import-layout probe: **passed** for Rhino, Rhino.Geometry, Point3d/Origin, rhinoscriptsyntax/AddLine, scriptcontext shim, and the local module.
- Version 0.4 parses Python traceback frames and publishes package-owned Visual Studio Error List tasks with zero-based navigation coordinates.

### Actual GUI checks (through v0.3.2)

- The VSIX loaded in a Visual Studio 2026 experimental instance.
- All five `Rhino:` commands were present under **Tools**.
- The package initialized successfully when its commands were invoked.
- **Configure Project IntelliSense** discovered and added exactly three Rhino CPython roots and generated the `scriptcontext` shim after confirmation.
- The IntelliSense test project opened and Visual Studio's bundled Pylance 2026.3.1 (Pyright 1.1.411) started.
- After restarting the experimental instance and reopening the configured solution, member completion worked for Rhino, Rhino.Geometry, Point3d/Origin, rhinoscriptsyntax/AddLine, scriptcontext/doc, and a local module.
- Point3d parameter signatures, QuickInfo documentation, and Go to Definition into the generated `Rhino\Geometry\__init__.pyi` were observed in the real editor.
- **Shift+F2** invoked the extension from a Python editor. An unsaved token was absent from disk before the keypress, saved by the command, and observed in the geometry created in Rhino.
- Successful `print` output, the exact Rhino PID/pipe, exit code, and completion marker appeared in the Rhino VS Bridge Output pane.
- An intentional Python exception showed its Unicode output, traceback, original script path and line 4, and exit code 1 in Visual Studio.
- A script path containing both spaces and Unicode characters executed successfully.
- With two Rhino instances, explicit selection changed only the selected instance. Closing that instance caused the next run to fail safely without falling back to the other Rhino process.
- The unavailable-selected-instance message was observed in the real GUI and explicitly stated that no script was sent.
- The v0.3.0 VSIX was tested with Visual Studio 2026 and Rhino 8.35. Live output, cooperative cancellation, restart after cancellation, Python tracebacks, RhinoCommon geometry creation, and prevention of overlapping executions all passed.
- The v0.3.1 VSIX was tested with Visual Studio 2026 and Rhino 8.36. Failed `rhinocode.exe` launch recovery, scripts longer than 15 seconds, delayed Rhino-side startup detection, startup-timeout workspace retention, cooperative cancellation before delayed startup, and overlap prevention all passed.
- The v0.3.2 VSIX was tested with Visual Studio 2026 and Rhino 8.36. After Rhino was blocked for about 60 seconds, the bridge reported the 15-second startup timeout, preserved the uncertain execution lock, rejected overlap with an accurate diagnostic, then accepted the delayed terminal result, released the slot automatically, and permitted a follow-up run without restarting Visual Studio.
- See [the end-to-end verification report](docs/end-to-end-workflow-vs2026.md) for the fixtures, checks, and limitations.

### Still to test or fix

- Improve the PTVS/Pylance refresh path: Project Reload displayed the new paths but Pylance 2026.3.1 retained its prior settings. A clean solution restart is currently required.
- Go to Definition through `rhinoscriptsyntax`'s star re-export was not confirmed; Point3d definition navigation is verified.
- `.py2` uses direct legacy CLI execution and does not yet return Python output or exception details to Visual Studio.
- Validate v0.4 Error List double-click navigation in the real Visual Studio 2026 GUI, including an external file with a Unicode/special-character path.
- CPython cancellation is cooperative; native calls are not forcibly interrupted, and legacy `.py2` cannot be cancelled safely.
- Install and load the generated VSIX in Visual Studio 2022; its command-line build is already passing.
- Incomplete execution workspaces are intentionally retained because Rhino may still be using them. Version 0.3.2 automatically reconciles a matching terminal result for 10 minutes and rechecks on Run or Cancel; permanently missing or malformed results remain blocked.

## Current milestones

- [x] Investigated RhinoCode CLI and VS SDK, documented clean-room architecture.
- [x] Wrote initial VSIX project with Tools menu commands and asynchronous CLI execution.
- [x] Added instance picker, output window, and Python stub path discovery with `.pyproj` backup.
- [x] Added stand-alone RhinoCode connectivity probe and initial XML/CLI tests.
- [x] Restored and built the VSIX with Visual Studio 2026 on Windows without warnings or errors.
- [x] Confirmed the v0.2.3 VSIX loads and all five commands from that version register in the Visual Studio 2026 experimental instance.
- [x] Confirmed official RhinoCode discovery and CPython script execution in an actual running Rhino 8 instance.
- [x] Confirmed RhinoCommon, rhinoscriptsyntax, scriptcontext, and local-module completion in Visual Studio 2026 Pylance.
- [x] Verify save-before-run, Shift+F2, Output capture, exception reporting, Unicode paths, and deterministic multi-instance execution.
- [x] Add no-timeout execution, cooperative cancellation, live output transport, safe workspace retention, and same-instance overlap prevention.
- [x] Verify the v0.3 streaming, cancellation, restart, traceback, RhinoCommon, and overlap-prevention workflow in Visual Studio 2026 with Rhino 8.35.
- [x] Add a Rhino-side startup acknowledgment, startup-only timeout, and safe process-launch recovery for v0.3.1.
- [x] Verify v0.3.1 startup timeout, delayed startup, long-running execution, cancellation, and launch recovery in Visual Studio 2026 with Rhino 8.36.
- [x] Add identity-validated automatic reconciliation, completion-aware cancellation, and state-specific diagnostics for v0.3.2.
- [x] Add Windows CI producing an installable `.vsix` artifact.
- [x] Perform the controlled v0.3.2 late-completion reconciliation GUI checks.
- [x] Add package-owned Error List integration and Python traceback navigation for v0.4.
- [ ] Perform the controlled v0.4 Error List and external-file navigation GUI checks.

## References

- RhinoCode CLI: https://developer.rhino3d.com/en/guides/scripting/advanced-cli/
- Rhino 8 Python 3: https://developer.rhino3d.com/guides/rhinopython/python-3-in-rhino/
- VS 2026 SDK-style VSIX: https://devblogs.microsoft.com/visualstudio/sdk-style-support-for-extension-projects/
- VS 2022/2026 compatibility: https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/extension-compatibility
- Visual Studio PTVS Search Paths: https://learn.microsoft.com/en-us/visualstudio/python/search-paths
- [v0.4 Python error navigation design and GUI checklist](docs/python-error-navigation-v04.md)
- Original extension (reference only): https://github.com/jingcheng-chen/RhinoPythonForVS

## Notes for Codex

Read `AGENTS.md`. Keep subsequent work on feature branches. IntelliSense, the Visual Studio 2026 end-to-end run workflow, and v0.3.2 execution reliability are verified through Rhino 8.36, including delayed completion after a startup timeout and automatic slot recovery. Version 0.4 Error List navigation is automated-test complete but still needs its controlled Visual Studio GUI pass. Do not add interactive debugging until the basic integration remains stable.
