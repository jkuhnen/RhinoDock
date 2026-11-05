# Rhino Dock

**Python development for Rhino 8 in Microsoft Visual Studio.**

Rhino Dock connects Microsoft Visual Studio 2026 to Rhino 8's CPython 3 environment. Edit Python scripts with project-based IntelliSense and execute them directly in Rhino.

## Features

- Run the active Python script in Rhino with **Shift+F2**.
- RhinoCommon, rhinoscriptsyntax and scriptcontext IntelliSense.
- Dedicated **Rhino Dock Python Project** template.
- Python print output in both Visual Studio and Rhino.
- Python traceback navigation to the source file and line.
- Select the target Rhino instance and cancel running scripts.

## Getting started

1. Install Visual Studio 2026 with the Python development workload and Rhino 8.
2. Install Rhino Dock.
3. In Rhino, open ScriptEditor once and run `StartScriptServer`.
4. Create a Rhino Dock Python Project or open a Python project.
5. Use **Tools > Rhino: Configure Project IntelliSense**.
6. Press **Shift+F2** in a saved Python script.

## Notes

Rhino 8.11 or later is required; the extension has been manually tested with Rhino 8.36 and Visual Studio 2026. Support for Visual Studio 2022 has not been manually verified. Standalone Python file IntelliSense and breakpoint debugging are not currently supported.

Rhino Dock was inspired by the older IronPython-based RhinoPythonForVS. It is an independent implementation using RhinoCode and does not reuse source code from that project.

Rhino Dock is open source under the MIT License, developed by Johannes Kuhnen.

[GitHub repository](https://github.com/jkuhnen/RhinoDock) | [Issues and support](https://github.com/jkuhnen/RhinoDock/issues) | [Releases](https://github.com/jkuhnen/RhinoDock/releases)
