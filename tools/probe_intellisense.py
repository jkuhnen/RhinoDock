"""Validate the files and search roots used for Rhino IntelliSense.

This is a deterministic static check. It does not replace the manual Visual
Studio/Pylance completion checks documented in the example project.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys


def version_key(path: Path) -> tuple[int, ...]:
    text = path.name.removeprefix("rhino3d-")
    try:
        return tuple(int(part) for part in text.split("."))
    except ValueError:
        return (0,)


def discover(root: Path) -> list[Path]:
    stub_parent = root / "site-stubs"
    versions = [
        path for path in stub_parent.glob("rhino3d-*")
        if (path / "Rhino" / "__init__.pyi").is_file()
    ]
    paths: list[Path] = []
    if versions:
        paths.append(max(versions, key=version_key))
    for name in ("site-rhinopython", "site-interop"):
        path = root / name
        if path.is_dir():
            paths.append(path)
    return paths


def contains(path: Path, pattern: str) -> bool:
    return path.is_file() and re.search(pattern, path.read_text(encoding="utf-8"), re.MULTILINE) is not None


def inspect(root: Path, project: Path) -> dict[str, object]:
    paths = discover(root)
    stub_root = paths[0] if paths else Path()
    geometry = stub_root / "Rhino" / "Geometry" / "__init__.pyi"
    rhino_python = root / "site-rhinopython"
    project_stub = project / ".rhino-vs-bridge" / "typings" / "scriptcontext.pyi"
    checks = {
        "Rhino": (stub_root / "Rhino" / "__init__.pyi").is_file(),
        "Rhino.Geometry": geometry.is_file(),
        "Rhino.Geometry.Point3d": contains(geometry, r"^class Point3d\("),
        "Point3d.Origin": contains(geometry, r"^\s+Origin:\s*Point3d\s*$"),
        "rhinoscriptsyntax": (rhino_python / "rhinoscriptsyntax.py").is_file(),
        "rhinoscriptsyntax.AddLine": contains(rhino_python / "rhinoscript" / "curve.py", r"^def AddLine\("),
        "scriptcontext": (rhino_python / "scriptcontext.py").is_file(),
        "scriptcontext.doc shim": contains(project_stub, r"^doc:\s*Rhino\.RhinoDoc\s*$"),
        "local module": (project / "local_module.py").is_file(),
    }
    return {"searchPaths": [str(path) for path in paths], "checks": checks}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--rhino-root", type=Path,
        default=Path.home() / ".rhinocode" / "py39-rh8",
    )
    parser.add_argument(
        "--project", type=Path,
        default=Path(__file__).resolve().parents[1] / "examples" / "intellisense_test",
    )
    args = parser.parse_args(argv)
    result = inspect(args.rhino_root.resolve(), args.project.resolve())
    print(json.dumps(result, indent=2))
    return 0 if all(result["checks"].values()) else 1


if __name__ == "__main__":
    sys.exit(main())
