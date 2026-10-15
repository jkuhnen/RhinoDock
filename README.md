# Rhino VS Bridge — Visual Studio 2026 / Rhino 8 (development preview)

Standalone **clean-room** prototype of a VSIX extension for writing Rhino 8 CPython scripts in **Visual Studio** (NOT VS Code), using Visual Studio's Python Tools for editing and IntelliSense. It runs saved scripts in an existing Rhino 8 instance by calling McNeel's official `rhinocode` CLI. Original inspiration: Jingcheng Chen's RhinoPythonForVS. None of that repository's source code was copied (its license has not been confirmed).

> **Important:** This is a source-code prototype, not a tested release. Python/XML smoke checks pass on Linux, but compiling and running the VSIX requires a Windows machine with Visual Studio 2026, the extension SDK, and Rhino 8. IntelliSense type stub recognition must be verified in Visual Studio itself. A prebuilt `.vsix` is **not** supplied yet.

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

1. **Visual Studio 2026**, with the **Visual Studio extension development** workload and **Python development** workload. A VS2022 SDK (API 17.x) is used so the resulting VSIX *should also* be compatible with Visual Studio 2022; both require real-world tests.
2. **Rhino 8.11 or newer**, with `rhinocode.exe` in `%ProgramFiles%\Rhino 8\System\`.
3. Open Rhino and enter the command **`StartScriptServer`**. Open **`ScriptEditor`** once to generate Python type stubs.
4. For editing and autocomplete, create or open a **Visual Studio Python project** (`.pyproj`) and choose a regular compatible CPython environment for its static analysis. Execution is **always** performed by Rhino, NOT by Visual Studio's selected Python interpreter.

## Compile first developer build

- Open `RhinoVSBridge.sln` in Visual Studio 2026.
- Restore NuGet packages. Build **Debug / Any CPU**. The SDK-style project uses `Microsoft.VisualStudio.SDK` 17.14 and `Microsoft.VSSDK.BuildTools` 18.5; package versions may be updated as needed.
- Use **F5** to launch the Visual Studio experimental instance, or install the generated `.vsix` after successful compilation.
- Under **Tools**, find five `Rhino:` menu commands.

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
- PTVS's support for McNeel's generated stubs needs to be tested. This code configures the search paths, **not** a new IntelliSense language server. In particular, type completion for Python.NET bindings is not guaranteed yet.

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

## Current milestones

- [x] Investigated RhinoCode CLI and VS SDK, documented clean-room architecture.
- [x] Wrote initial VSIX project with Tools menu commands and asynchronous CLI execution.
- [x] Added instance picker, output window, and Python stub path discovery with `.pyproj` backup.
- [x] Added stand-alone RhinoCode connectivity probe and initial XML/CLI tests.
- [ ] Build/restore in Visual Studio 2026 on Windows; fix compiler or manifest issues.
- [ ] Confirm VSIX loads and commands work with actual Rhino 8.
- [ ] Confirm `Rhino.Geometry` and `rhinoscriptsyntax` member IntelliSense in PTVS; improve if necessary.
- [ ] Add reliable cancellation, keyboard shortcut defaults, traceback navigation, more tests, configuration persistence.
- [ ] Add CI producing an installable `.vsix` and a private GitHub repository.

## References

- RhinoCode CLI: https://developer.rhino3d.com/en/guides/scripting/advanced-cli/
- Rhino 8 Python 3: https://developer.rhino3d.com/guides/rhinopython/python-3-in-rhino/
- VS 2026 SDK-style VSIX: https://devblogs.microsoft.com/visualstudio/sdk-style-support-for-extension-projects/
- VS 2022/2026 compatibility: https://learn.microsoft.com/en-us/visualstudio/extensibility/migration/extension-compatibility
- Visual Studio PTVS Search Paths: https://learn.microsoft.com/en-us/visualstudio/python/search-paths
- Original extension (reference only): https://github.com/jingcheng-chen/RhinoPythonForVS

## Notes for Codex

Read `AGENTS.md`. The first meaningful next task is to compile and test this VSIX against a real Windows Visual Studio 2026 + Rhino 8 environment, **before** adding larger features. Avoid claiming this source-only prototype is installed or finished.
