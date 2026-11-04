# Rhino VS Bridge

Rhino VS Bridge connects **full Microsoft Visual Studio 2026** to Rhino 8 for editing and running Rhino Python 3 scripts. It provides PTVS IntelliSense for Rhino APIs, direct execution through RhinoCode, live output, cancellation, execution recovery, and navigation from Python runtime errors to the corresponding source line.

> Rhino VS Bridge is for Microsoft Visual Studio, not Visual Studio Code (VS Code).

The stable release is **v0.4.0**. Breakpoint debugging and `debugpy` integration are not supported.

## Background and motivation

Inspired by [Jingcheng Chen's RhinoPythonForVS](https://github.com/jingcheng-chen/RhinoPythonForVS), Rhino VS Bridge revisits the idea of developing and running Rhino Python scripts from Visual Studio. Rhino 8's CPython 3 support and RhinoCode scripting infrastructure make a new integration possible: an independent implementation for Visual Studio 2026 that uses RhinoCode instead of the legacy IronPython 2.7 and CodeListener workflow.

## Main features

- Rhino 8 CPython script execution from Visual Studio with **Shift+F2**.
- RhinoCommon, `rhinoscriptsyntax`, and `scriptcontext` IntelliSense through the Microsoft Python development workload.
- Live standard output and error output in the **Rhino VS Bridge** Output pane.
- Cooperative cancellation for running scripts.
- Native Visual Studio Error List entries that open Python files at traceback locations, including files outside the current solution.
- Explicit Rhino-instance selection and conservative recovery when execution state is uncertain.
- No Rhino assemblies loaded into the Visual Studio process.

## Download

Download `RhinoVSBridge-v0.4.0.vsix` from the [v0.4.0 GitHub Release](https://github.com/jkuhnen/RhinoVSBridge/releases/tag/v0.4.0). The release also provides installation instructions and a SHA-256 checksum.

The VSIX is currently unsigned, so Visual Studio may display an unknown-publisher warning. Verify that the checksum matches the published `SHA256SUMS.txt` before installing it.

## Quick Start

No Visual Studio extension-development experience is required.

1. Install **Visual Studio 2026** with the **Python development** workload, and install **Rhino 8**.
2. Download `RhinoVSBridge-v0.4.0.vsix`, close Visual Studio, run the VSIX installer, and restart Visual Studio.
3. Start Rhino, open Rhino's **ScriptEditor** once, then run `StartScriptServer` in Rhino's command line. Keep Rhino open.
4. Open or create a Python project in Visual Studio. Choose **Tools > Rhino: Configure Project IntelliSense**, then reload the project if Visual Studio asks.
5. Open a `.py` file and press **Shift+F2** to run it in Rhino.

A minimal RhinoCommon example:

```python
import Rhino
import scriptcontext

point = Rhino.Geometry.Point3d(10, 20, 0)
scriptcontext.doc.Objects.AddPoint(point)
scriptcontext.doc.Views.Redraw()
print("Added point:", point)
```

Execution messages and Python output appear under **View > Output**; select **Rhino VS Bridge** in the output-source list. Python runtime failures also appear in **View > Error List**. Double-click a Rhino VS Bridge error to open its file at the reported line. To request cancellation, choose **Tools > Rhino: Cancel Running Script**.

During verified first-run testing, IntelliSense became available after **Rhino: Configure Project IntelliSense** and the first Shift+F2 execution. Running a script is not intended to be a technical prerequisite for IntelliSense; if completions do not appear immediately, follow the troubleshooting steps below.

`CodeListener` is not required. Rhino VS Bridge uses Rhino 8's official RhinoCode command-line interface.

## Requirements

- Microsoft Visual Studio 2026 with the **Python development** workload.
- Rhino 8.11 or later; the stable release was manually verified with Rhino 8.36.
- Rhino's ScriptServer running in the Rhino instance that should execute the script.
- A Visual Studio Python project for project-level IntelliSense configuration.

Visual Studio 2022 support is an architectural target but is not claimed as manually verified for v0.4.0.

## Troubleshooting

### Rhino commands do not appear in Visual Studio

Confirm that the VSIX is enabled under **Extensions > Manage Extensions**, then restart Visual Studio. This extension does not install into VS Code.

### Visual Studio cannot connect to Rhino

Start Rhino, open ScriptEditor once, and run `StartScriptServer`. In Visual Studio, choose **Tools > Rhino: Diagnose Connection and Stubs**. If several Rhino instances are open, select the intended instance explicitly.

### Rhino API completions do not appear

Verify that the Visual Studio Python development workload is installed. Run **Tools > Rhino: Configure Project IntelliSense**, accept any project reload prompt, and allow Visual Studio time to index the generated search paths. Reopen the project if necessary.

### Shift+F2 does not run the script

Use **Tools > Rhino: Run Active Python Script** to check whether another extension has claimed the keyboard shortcut. Make sure the active document is a saved `.py` file.

### A previous execution is still blocking new runs

Check the Rhino VS Bridge Output pane. The extension deliberately blocks overlapping executions while Rhino may still be running a script. It releases the slot automatically only after it validates a terminal result for that exact execution and Rhino instance. Restart Rhino and Visual Studio only if the execution cannot be reconciled.

## Known limitations

- Breakpoint debugging, debugger attachment, and `debugpy` integration are not supported.
- Execution is save-and-run; there is no automatic file watching or hot reload.
- Cancellation is cooperative and cannot interrupt code that does not yield control in Rhino.
- A genuinely unresolved Rhino execution remains blocked to prevent accidental overlap.
- The publisher is unsigned, so the installer displays a warning.
- The first stable release has been manually verified with Visual Studio 2026 and Rhino 8.36; other combinations require independent testing.

## Documentation

- [Installation and usage guide](docs/installation-v040.md)
- [End-to-end Visual Studio 2026 workflow](docs/end-to-end-workflow-vs2026.md)
- [Execution reliability and recovery](docs/execution-reliability-v032.md)
- [IntelliSense integration](docs/intellisense-vs2026.md)
- [Python error navigation](docs/python-error-navigation-v04.md)
- [Acknowledgements and project lineage](docs/acknowledgements.md)
- [v0.4.0 release notes](docs/releases/v0.4.0.md)

## Acknowledgements

Rhino VS Bridge was conceptually inspired by [Jingcheng Chen's RhinoPythonForVS](https://github.com/jingcheng-chen/RhinoPythonForVS), the pioneering Visual Studio integration for Rhino Python created at [DesignToProduction](https://www.designtoproduction.com/) and shared as an original open-source project. RhinoPythonForVS demonstrated the value of editing, IntelliSense, and send-to-Rhino execution from Visual Studio.

Rhino VS Bridge is an independent modern implementation for Rhino 8 CPython and Visual Studio 2026. It uses McNeel's RhinoCode CLI rather than CodeListener and does not import the original project's source code or assets. Jingcheng Chen and DesignToProduction have not contributed to, reviewed, or endorsed Rhino VS Bridge.

See [Acknowledgements and project lineage](docs/acknowledgements.md) for the technical relationship and attribution details.

## License

Rhino VS Bridge is open-source software licensed under the [MIT License](LICENSE).

The acknowledgement of RhinoPythonForVS records conceptual inspiration only. No license is inferred for that project, and its source code and assets are not included in Rhino VS Bridge.
