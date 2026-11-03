# Rhino VS Bridge v0.4.0 installation and usage

## Requirements

- Windows with 64-bit Visual Studio 2026 Community, Professional, or Enterprise.
- The Visual Studio **Python development** workload. The extension-development
  workload is needed only to build Rhino VS Bridge from source.
- Rhino 8.11 or newer; v0.4.0 was verified with Rhino 8.36.
- A saved Visual Studio Python project (`.pyproj`) is recommended for project
  IntelliSense configuration.

Rhino VS Bridge runs scripts inside Rhino through McNeel's official
`rhinocode.exe`. Visual Studio's selected Python interpreter is used only for
editing and static analysis.

## Install the VSIX

1. Close all Visual Studio instances.
2. Double-click `RhinoVSBridge-v0.4.0.vsix`.
3. In the Visual Studio VSIX Installer, select the normal Visual Studio 2026
   installation and complete the per-user installation.
4. Start Visual Studio and confirm that the six commands beginning with
   **Rhino:** appear under **Tools**.

The manifest targets 64-bit Visual Studio 17.0 or newer. A Community target is
also compatible with Professional and Enterprise editions under the Visual
Studio VSIX compatibility rules. The package requires .NET Framework 4.8 and
the Visual Studio core editor. It does not depend on an experimental Visual
Studio instance. Uninstall it later from **Extensions > Manage Extensions**.

## Start Rhino and configure IntelliSense

1. Start Rhino 8.
2. Run Rhino's **StartScriptServer** command. This is required in each Rhino
   session that should accept scripts.
3. Open Rhino's **ScriptEditor** once so Rhino generates its Python type stubs.
4. Open a Visual Studio Python project and choose
   **Tools > Rhino: Diagnose Connection and Stubs**.
5. Choose **Tools > Rhino: Configure Project IntelliSense**. The bridge creates
   a backup of the project file before adding Rhino's generated search paths.
6. Close and reopen the solution so Pylance reloads the updated paths.

If no Python project is selected, the configuration command copies the paths
to the clipboard instead of changing a project.

### First-run IntelliSense note

In the final Visual Studio 2026 and Rhino 8.36 installation test, completion for
`RhinoCommon`, `rhinoscriptsyntax`, and `scriptcontext` appeared after
running **Rhino: Configure Project IntelliSense** and then performing the first
**Shift+F2** execution. This is the observed first-run sequence, not evidence
that script execution is technically required to activate IntelliSense. If
completion does not appear immediately after configuration, close and reopen
the solution, confirm Pylance has reloaded the project search paths, and perform
one normal bridge execution before investigating further.

## Run scripts and inspect errors

1. Open a saved `.py` file in the Visual Studio editor.
2. Press **Shift+F2**, or choose
   **Tools > Rhino: Run Active Python Script**. Unsaved editor changes are saved
   before execution. With multiple Rhino instances, select the intended one.
3. Read live stdout, stderr, completion, cancellation, and recovery diagnostics
   in **Output > Rhino VS Bridge**.
4. For Python exceptions, open **View > Error List**. Rhino VS Bridge entries
   preserve the traceback message; double-click an entry to open its file and
   navigate to the reported line, including files outside the solution.
5. Use **Tools > Rhino: Cancel Running Script** for cooperative CPython
   cancellation.

Do not use Visual Studio's ordinary Python Run or Debug command for Rhino
scripts. Version 0.4.0 does not provide breakpoint debugging or `debugpy`
integration.

## Known limitations

- IntelliSense configuration currently needs a solution restart to refresh
  Pylance reliably.
- Cancellation is cooperative and can be delayed by blocking native calls.
- Legacy `.py2` execution has no structured live-output, cancellation, or Error
  List protocol.
- Output-pane traceback text is not clickable; the supported navigation surface
  is Visual Studio's Error List.
- Error navigation with Unicode and punctuation is covered by automated tests;
  the complete special-character path case still needs a normal-instance GUI
  verification.

## Verified normal-instance workflow

The final v0.4.0 VSIX passed manual installation testing in a regular,
non-experimental Visual Studio 2026 instance with Rhino 8.36:

- installation through the normal Visual Studio VSIX Installer;
- registration of all six **Rhino:** commands;
- Rhino connection and stub diagnostics;
- execution through **Shift+F2**;
- completion for RhinoCommon, `rhinoscriptsyntax`, and `scriptcontext`;
- Python runtime errors in the Error List; and
- double-click navigation to the exact traceback line, including the line 7
  regression case.
