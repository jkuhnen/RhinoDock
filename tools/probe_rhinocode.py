#!/usr/bin/env python3
"""Portable diagnostic for Rhino 8 rhinocode CLI (runs with ordinary Python 3).

Examples:
  python tools/probe_rhinocode.py
  python tools/probe_rhinocode.py --executable "C:/Program Files/Rhino 8/System/rhinocode.exe"
  python tools/probe_rhinocode.py --run examples/hello_rhino.py --instance rhinocode_remotepipe_1234
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def discover_executable() -> str:
    program_files = os.environ.get("ProgramFiles", "C:/Program Files")
    candidate = Path(program_files) / "Rhino 8" / "System" / "rhinocode.exe"
    return str(candidate) if candidate.exists() else "rhinocode"


def invoke(executable: str, *args: str, timeout: int = 60) -> subprocess.CompletedProcess:
    return subprocess.run([executable, *args], capture_output=True, text=True, timeout=timeout, check=False)


def list_instances(executable: str) -> list[dict]:
    result = invoke(executable, "list", "--json", timeout=15)
    if result.returncode:
        raise RuntimeError("rhinocode list failed: " + (result.stderr or result.stdout))
    raw = result.stdout.strip()
    start, end = raw.find("["), raw.rfind("]")
    if start == -1 or end < start:
        raise ValueError("rhinocode did not output the expected JSON array: " + raw[:300])
    data = json.loads(raw[start:end + 1])
    if not isinstance(data, list):
        raise TypeError("Unexpected Rhino instance list")
    return data


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", default=discover_executable())
    parser.add_argument("--run", type=Path, help="Run a saved .py file in Rhino")
    parser.add_argument("--instance", help="pipeId from list --json")
    args = parser.parse_args(argv)
    print("RhinoCode executable:", args.executable)
    try:
        instances = list_instances(args.executable)
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as exc:
        print("ERROR:", exc, file=sys.stderr)
        print("Check Rhino 8 >= 8.11 and run StartScriptServer in Rhino.", file=sys.stderr)
        return 1
    for instance in instances:
        print(f"PID={instance.get('processId')}  ID={instance.get('pipeId')}  document={instance.get('activeDoc', {}).get('title')}")
    if not instances:
        print("No active Rhino script servers. Run StartScriptServer in Rhino.")
        return 1
    if args.run:
        if not args.run.is_file() or args.run.suffix.lower() not in {".py", ".py2"}:
            print("The script must be an existing .py or .py2 file.", file=sys.stderr)
            return 2
        pipe_id = args.instance or (instances[0].get("pipeId") if len(instances) == 1 else None)
        if not pipe_id:
            print("Multiple Rhino instances found: pass --instance PIPE_ID.", file=sys.stderr)
            return 2
        result = invoke(args.executable, "--rhino", pipe_id, "script", str(args.run.resolve()), timeout=600)
        print(result.stdout)
        if result.stderr:
            print(result.stderr, file=sys.stderr)
        return result.returncode
    return 0


if __name__ == "__main__":
    sys.exit(main())
