# Acknowledgements and project lineage

## Jingcheng Chen and RhinoPythonForVS

Rhino Dock (originally released as Rhino VS Bridge) was conceptually inspired by [Jingcheng Chen's RhinoPythonForVS](https://github.com/jingcheng-chen/RhinoPythonForVS), the pioneering Visual Studio integration for Rhino Python created at [DesignToProduction](https://www.designtoproduction.com/) and shared as an original open-source project. RhinoPythonForVS demonstrated the value of editing, IntelliSense, and send-to-Rhino execution from Visual Studio.

Rhino Dock is an independent modern implementation for Rhino 8 CPython and Visual Studio 2026. It uses McNeel's RhinoCode CLI rather than CodeListener and does not import the original project's source code or assets. Jingcheng Chen and DesignToProduction have not contributed to, reviewed, or endorsed Rhino Dock.

## Conceptual relationship

The projects address the same high-level workflow: edit Rhino Python in full Microsoft Visual Studio, obtain useful language assistance, and send a script to Rhino for execution. Their implementations and supported environments differ substantially.

| Area | RhinoPythonForVS | Rhino Dock |
| --- | --- | --- |
| Python runtime | IronPython-era Rhino Python | Rhino 8 CPython 3 |
| Visual Studio generation | Visual Studio 2015 era | Visual Studio 2026 |
| Rhino transport | CodeListener | Official RhinoCode CLI and ScriptServer |
| Rhino process handling | Original project workflow | Explicit instance discovery and selection |
| Execution reliability | Original project behavior | Live output, cancellation, startup acknowledgement, and state reconciliation |
| Error navigation | Original project behavior | Native Visual Studio Error List integration |

`CodeListener` is not required by Rhino Dock.

## Source and license boundary

The publication audit compared the current Rhino Dock repository and its available Git history with the public RhinoPythonForVS repository and its available history. It found no shared source blobs, copied assets, original project identifiers, or original authorship in Rhino Dock. The implementation was developed independently around Rhino 8's supported RhinoCode interface.

RhinoPythonForVS describes itself as an open-source project in its README, but the audit did not find an identifiable license file or license grant in its current tree or available Git history. No license terms should therefore be inferred for its code or assets. The acknowledgement above records conceptual inspiration; it is not a claim of source-code reuse, permission, contribution, partnership, or endorsement.

Rhino Dock is independently licensed under the [MIT License](../LICENSE). That license applies only to Rhino Dock and does not grant or imply rights to RhinoPythonForVS source code or assets.
