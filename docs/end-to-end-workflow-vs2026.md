# End-to-end workflow verification — Visual Studio 2026 and Rhino 8

Verified on 2026-10-15 with Visual Studio Community 2026 18.11, Rhino 8.35,
and Rhino's official `rhinocode.exe`. The tests used an isolated Visual Studio
root suffix and a running Rhino ScriptServer.
This report records the v0.2.3 workflow verification. Version 0.3 replaces its
fixed timeout and temporary runner lifecycle; see [the v0.3 reliability report](execution-reliability-v03.md).


## How execution works

`Rhino: Run Active Python Script` saves the active `.py` document, resolves an
explicit Rhino pipe, and asks the official RhinoCode CLI to execute a temporary
CPython runner inside Rhino. That runner executes the original source with its
real file name and directory, captures `stdout`, `stderr`, and uncaught Python
exceptions, then returns a small JSON result to the extension. No Rhino DLL is
loaded into Visual Studio and Visual Studio's Python interpreter does not run
the script.

The runner is necessary because the CLI reports that a script request was
accepted, but does not itself propagate Python `print` output, tracebacks, or a
failed Python exit status back to the caller.

## Actual GUI tests

The following checks were performed in the real Visual Studio and Rhino GUIs,
not inferred from unit tests:

- VSIX 0.2.3 loaded in Visual Studio 2026 and all five `Rhino:` commands were
  visible under **Tools**.
- Visual Studio exposed `Tools.RhinoRunActivePythonScript` with the effective
  binding `Text Editor::Shift+F2`.
- An editor-only token change was confirmed absent from disk before pressing
  Shift+F2. The command saved it, executed that changed value in Rhino, and the
  verifier found the named point at `(125, 250, 75)` in the selected Rhino
  document.
- A successful script's Unicode `print` text, target PID/pipe, exit code zero,
  and completion marker appeared in **Output > Rhino VS Bridge**.
- `e2e_print_and_error.py` printed before raising its intentional exception.
  Visual Studio displayed the `RuntimeError`, traceback, original source path,
  and original line 4, followed by exit code 1 and an actionable error dialog.
- A script copied to a path containing spaces and Unicode characters
  (`Rhino VS Bridge Grüße 世界\mein Test ü.py`) executed successfully.
- With two Rhino instances running, the second pipe was explicitly selected.
  Only that instance gained the test point; the primary instance's object count
  was unchanged.
- After the selected second instance was closed, the next run was refused. The
  extension cleared the stale selection, reported that no script was sent, and
  did not silently fall back to the remaining Rhino instance.

## Automated checks

Run both suites from the repository root:

```powershell
dotnet run --project tests\RhinoVSBridge.CoreTests
python -m unittest discover -s tests -v
```

The C# suite links the actual product sources and covers Windows argument
quoting, spaces and Unicode, JSON schema additions, malformed discovery output,
output/traceback formatting, missing executables, deterministic target
selection, obsolete IntelliSense path removal, and `.pyproj` backup behavior.
The portable Python suite checks the VSIX command/shortcut contract, official
RhinoCode protocol usage, CPython capture-runner contract, project portability,
stub discovery, and XML/manifest structure.

For an opt-in test against a running Rhino instance:

```powershell
dotnet run --project tests\RhinoVSBridge.CoreTests -- \
  --rhino-integration rhinocode_remotepipe_<pid> examples\intellisense_test\e2e_workflow.py
```

This command changes the active Rhino document by adding a point.

## Remaining limitations

- `.py2` remains a direct legacy RhinoCode invocation; Python output and
  exceptions are not captured with the CPython runner.
- The traceback can include one bridge-runner frame before the original script
  frame. Original file names and line numbers are retained, but clickable
  traceback navigation into Visual Studio is not implemented.
- This v0.2.3 test used a five-minute timeout and had no cancellation command.
  Those historical limitations are replaced by the v0.3 reliability design.
- The no-ScriptServer and mid-execution interruption messages have automated
  coverage and defensive handling, but the user's primary Rhino session was
  not deliberately stopped during the GUI tests.
- VSIX loading and Shift+F2 were verified in Visual Studio 2026. A command-line
  build passes with Visual Studio 2022, but its GUI load remains unverified.
- Interactive debugging is deliberately outside this milestone.
