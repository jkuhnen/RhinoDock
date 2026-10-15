# Execution reliability for v0.3

This milestone keeps Python execution inside Rhino 8 through McNeel's official
`rhinocode.exe`. It does not add interactive debugging, load RhinoCommon into
Visual Studio, or alter Rhino's Python installation.

## Runtime behavior

For CPython `.py` files, the bridge creates a private execution workspace under
`%LOCALAPPDATA%\RhinoVSBridge\Executions\<session>`. The workspace contains:

- `bridge_runner.py`, which Rhino executes;
- `output.jsonl`, an append-only stream of Python stdout/stderr events;
- `result.json`, written atomically only after the runner has left user code;
- `cancel.requested`, created when Visual Studio requests cancellation; and
- `session.txt`, containing the original source path and selected Rhino pipe.

There is no normal script timeout. A zero exit code from `rhinocode.exe` means
that the request was accepted, not that Python execution finished. The bridge
therefore continues watching the event stream and waits for the atomic result.
It terminates a stuck CLI transport only after that result proves that the
Python runner has finished.

The runner compiles the original file with its original absolute path and sets
`__file__` to that path. Tracebacks therefore retain the user's source file and
line numbers. The original script directory is temporarily first on `sys.path`,
so local imports behave like direct script execution.

## Streaming and cancellation

Writes to Python `sys.stdout` and `sys.stderr` are mirrored to the JSON-lines
event file and forwarded to **Output > Rhino VS Bridge** while the script runs.
The complete captured streams are still included in the final result.

**Rhino: Cancel Running Script** creates the cancellation sentinel for the
execution associated with the selected Rhino pipe. A Python trace function
raises a private `BaseException` inside Rhino CPython at the next Python trace
point. The bridge deliberately does not treat killing `rhinocode.exe` as
cancellation, because the script may continue inside Rhino.

Cancellation is cooperative. It is prompt for ordinary Python loops but can be
delayed while user code is blocked in a native RhinoCommon/.NET call, a sleep,
or other code that does not return to a Python trace point. Legacy `.py2`
execution cannot be cancelled safely by this mechanism and remains a direct
RhinoCode invocation.

The coordinator rejects another bridge execution for an already occupied Rhino
pipe. Visual Studio's command handler also serializes bridge runs globally, so
the extension cannot accidentally overlap scripts in one Rhino instance.

## Interrupted connections and cleanup

An incomplete workspace is never automatically deleted. If dispatch might have
reached Rhino and no atomic result exists, Rhino may still be reading or running
the file. The bridge retains the workspace, reports its path, and keeps the
selected Rhino execution slot occupied.

At package startup, only completed workspaces older than one day are removed.
Incomplete workspaces are reported in the Output pane. Delete one manually only
after confirming that its Rhino script is no longer running. Cancellation can
still be requested for a retained workspace while the Visual Studio session is
open.

## Automated verification

Run from the repository root:

```powershell
dotnet build RhinoVSBridge.sln -c Debug
dotnet run --project tests\RhinoVSBridge.CoreTests -c Debug --no-build
python -m unittest discover -s tests -p "test_*.py" -v
```

Results on 2026-10-15:

- VSIX build: passed with 0 warnings and 0 errors.
- Product C# tests: 15/15 passed.
- Portable Python tests: 13/13 passed.
- Tests cover infinite transport waits, original-path compilation, streaming
  and cancellation runner contracts, same-instance overlap rejection,
  interruption retention, completed-only cleanup, argument quoting, instance
  selection, and IntelliSense configuration.
- The GitHub Actions workflow uses the `windows-2025-vs2026` hosted image,
  rebuilds and tests the solution, and uploads the Release VSIX artifact.

## Actual GUI verification

Previously verified in Visual Studio 2026 and Rhino 8.35:

- save-before-run and Shift+F2 execution;
- target-pipe selection with two Rhino instances;
- success output, exceptions, Unicode paths, and original traceback locations;
- no fallback to a different Rhino instance after the selected instance closed.

Not yet genuinely verified for the v0.3 reliability code:

- the sixth Tools command in a freshly installed v0.3 VSIX;
- live Output-pane streaming during a long script;
- acknowledgement of **Cancel Running Script** inside Rhino; and
- recovery from a deliberately interrupted live ScriptServer connection.

A command-line live attempt on 2026-10-15 found Rhino 8.35 through
`rhinocode list --json`, and the CLI accepted test submissions with exit code
zero, but that long-running Rhino session did not process the submitted fixture.
Restarting `StartScriptServer` through UI automation did not change that state.
All potentially submitted test workspaces were retained and given cancellation
sentinels. This result is recorded as inconclusive, not as a successful GUI
test. Restart Rhino, run `StartScriptServer`, install the v0.3 VSIX, then use
`examples/reliability/streaming_and_cancel.py` for the remaining manual check.

## Deliberate limitations

- Cancellation is cooperative, not interactive debugging.
- Native calls cannot be forcibly interrupted safely by this bridge.
- `.py2` has no runner-based streaming or safe cancellation.
- Traceback text is not converted into clickable Error List entries.
- Automatic cancellation of abandoned executions after Visual Studio exits is
  not possible without an in-Rhino service and is not claimed.
