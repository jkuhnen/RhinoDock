# Visual Studio 2026 Pylance and Rhino 8 IntelliSense

Verified on 2026-10-15 with Visual Studio Community 2026 18.11, PTVS, Pylance
2026.3.1 (Pyright 1.1.411), Rhino 8.35, and Rhino's CPython 3.9 stub tree.
This document concerns Visual Studio, not Visual Studio Code.

## Automatic setup for new Rhino Dock projects (v0.6.0)

The **Rhino Dock Python Project** template now includes a Visual Studio `IWizard`
extension that discovers locally generated RhinoCode typing directories **before**
PTVS loads the new project. It injects the resolved paths into the project's
`<SearchPath>` element, and creates the project-local
`.rhino-dock/typings/scriptcontext.pyi` shim after generation. This avoids
editing a loaded `.pyproj` file and eliminates a required project reload on
new project creation.

**Before creating the project**, launch Rhino 8 and open the ScriptEditor at
least once to generate the `.rhinocode/py39-rh8` stubs on that computer.
Creating the project without Rhino stubs still works, but only the local shim
path will be present; after Rhino has generated its stubs, use
**Tools > Rhino: Configure Project IntelliSense** once and reopen the solution.
The same manual command remains appropriate for existing Python projects.

This automatic setup has automated contract/build coverage. It requires a
manual end-to-end verification of a newly created project in Visual Studio
2026 before v0.6.0 is released publicly.

## How PTVS passes paths to Pylance

The Visual Studio Python project system reads the semicolon-separated
`<SearchPath>` value from the `.pyproj`, resolves relative entries against
`ProjectHome`, and exposes absolute paths through its language-client project
context. The PTVS language client appends those project paths to the global
Python **Analysis > Search paths** values and returns the combined list to
Pylance as `python.analysis.extraPaths` during `workspace/configuration`.
Changing a PTVS search-path node raises `workspace/didChangeConfiguration`.

The two similarly named Tools > Options > Python > Analysis fields have
different purposes:

- **Search paths** is a multiline/semicolon-separated global list. PTVS sends
  it as `python.analysis.extraPaths`, then appends project search paths.
- **Stubs path** is one global directory sent as `python.analysis.stubPath`.
  It is intended for a custom stub tree, not for Rhino's combination of a
  versioned `.pyi` package root and ordinary Python source roots.

For Rhino projects, use project Search Paths. Leaving the global fields empty
prevents one machine's Rhino paths from affecting unrelated Python projects.

## Rhino 8 layout and the resolution issue

The relevant generated/install-local directories are:

```text
%USERPROFILE%\.rhinocode\py39-rh8\
  site-stubs\rhino3d-<Rhino-build>\
    Rhino\__init__.pyi
    Rhino\Geometry\__init__.pyi  # class Point3d, Origin, constructors, docs
    System\...
  site-rhinopython\
    rhinoscriptsyntax.py          # public star re-exports
    rhinoscript\curve.py          # AddLine implementation/signature/docs
    scriptcontext.py              # replaces itself in sys.modules dynamically
  site-interop\
```

Pylance must receive the versioned directory that directly contains `Rhino`,
not the parent `site-stubs` directory. The previous prototype also added the
legacy Rhino IronPython `settings\lib` directory, which could compete with the
Rhino 8 CPython sources. Both entries have been removed.

Pylance can analyze the public re-exports in `rhinoscriptsyntax.py`. It cannot
infer all module-level members created by `scriptcontext.py`, because that file
replaces `sys.modules[__name__]` with a runtime object. The bridge therefore
generates a small project-local `.pyi` shim for `scriptcontext`; it does not
modify or copy anything into Rhino's Python installation.

## Reproducible setup

1. Open Rhino's ScriptEditor once so Rhino generates its stubs.
2. Open a `.pyproj` in Visual Studio.
3. Run **Tools > Rhino: Configure Project IntelliSense**.
4. Close and reopen the solution (or restart the experimental Visual Studio
   instance) before testing Pylance.
5. Open `examples/intellisense_test/intellisense_probe.py` and use the probes
   listed at the bottom of that file.

The tracked example contains only the portable relative
`.rhino-vs-bridge\typings` entry. The command discovers and appends the three
absolute Rhino paths for the current machine, after backing up the `.pyproj`.

On the tested VS 18.11 build, Solution Explorer showed newly reloaded paths,
but the already running Pylance 2026.3.1 service logged `Settings have not
changed. Skipping update.` and did not refresh Rhino imports. Closing and
reopening the solution caused the new Pylance service to consume the paths and
worked reliably. This is a PTVS/Pylance lifecycle limitation; the bridge now
states the required restart explicitly.

Run the deterministic non-GUI checks with:

```powershell
python tools\probe_intellisense.py
python -m unittest discover -s tests -v
```

The probe inspects import roots and expected definitions only. It deliberately
does not import Rhino outside Rhino and is not a substitute for the GUI check.

## Features actually observed in Visual Studio 2026

After a clean solution restart, the real Pylance editor UI provided:

- member completion for `Rhino.Geometry`, `Rhino.Geometry.Point3d`,
  `Point3d.Origin`, `rhinoscriptsyntax.AddLine`, `scriptcontext.doc`, and the
  local `make_label` function;
- Point3d constructor signature help: `(x: float, y: float, z: float) -> Point3d`;
- Point3d QuickInfo with its generated Rhino documentation;
- Go to Definition from `Point3d` to the generated
  `Rhino\Geometry\__init__.pyi` file.

Pylance started as the Visual Studio-bundled server, version 2026.3.1. No new
language server was introduced. Go to Definition through the
`rhinoscriptsyntax` star re-export was not confirmed, although its completion
and callable source definition were resolved. Interactive debugging inside
Rhino remains outside this feature.
