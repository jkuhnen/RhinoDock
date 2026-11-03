# Execution reliability for v0.3.1

Version 0.3.1 builds on the GUI-tested v0.3.0 execution path. It keeps normal
Python execution, live output, cooperative cancellation, original tracebacks,
and per-Rhino overlap prevention unchanged. The release adds a bounded startup
phase and reliable recovery when `rhinocode.exe` cannot dispatch a request.

## Startup protocol

Each CPython execution still uses a private directory under
`%LOCALAPPDATA%\RhinoVSBridge\Executions\<session>`. The generated runner now
writes `started.json` atomically as its first Rhino-side protocol action. That
file proves that Rhino CPython opened and began executing the bridge runner. It
is separate from `result.json`, which continues to prove that user code has
finished.

The bridge waits up to 15 seconds for the startup acknowledgment by default.
The timeout is configurable through `RhinoCli.ScriptStartupTimeout`, which also
makes short deterministic test intervals possible. There is currently no
persisted user-facing option for this value.

The startup timeout ends as soon as `started.json` appears. After that point
there is no script-duration timeout: a normally running script may take as long
as needed. A delayed acknowledgment within the configured interval is accepted
normally.

If the timeout expires, the CLI transport is stopped but the execution is not
declared finished. The request may have reached Rhino and may start later. The
workspace is retained, the selected Rhino execution slot remains occupied, and
a second script cannot be sent to that instance. **Cancel Running Script** can
still create `cancel.requested`; a runner that starts late checks that sentinel
before compiling user code.

## Launch failures and safe recovery

The bridge distinguishes failures that cannot have started Rhino-side code from
uncertain failures:

| Observation | Cleanup and execution state |
| --- | --- |
| `Process.Start` fails or throws | Delete the incomplete workspace, release the slot, and allow retry. |
| `rhinocode.exe` exits nonzero immediately, before acknowledgment | Report its output, delete the workspace, release the slot, and allow retry. |
| No acknowledgment before the startup timeout | Preserve the workspace and keep the slot occupied. |
| Transport disconnects after the immediate-start window or after acknowledgment | Preserve the workspace and keep the slot occupied. |
| Atomic `result.json` appears | Execution is final; clean up normally and release the slot. |

An immediate nonzero exit is treated as an authoritative CLI rejection only
before the one-second immediate-failure window closes and while no Rhino-side
acknowledgment exists. Later failures remain conservative because Rhino may
still be executing.

Output-reader tasks and process objects are disposed on every completed return
path. A launch failure returns a structured dispatch failure to the package, so
the command handler releases the coordinator entry even though it reports an
error to the user.

## Automated verification

Run from the repository root:

```powershell
dotnet build RhinoVSBridge.sln -c Debug
dotnet run --project tests\RhinoVSBridge.CoreTests -c Debug --no-build
python -m unittest discover -s tests -p "test_*.py" -v
```

The v0.3.1 tests cover:

- successful startup acknowledgment and completed cleanup;
- delayed acknowledgment followed by execution beyond the startup timeout;
- missing acknowledgment, retained workspace, overlap rejection, and a late
  cancellation sentinel;
- missing `rhinocode.exe`, immediate nonzero exit, cleanup, and subsequent slot
  acquisition;
- delayed transport disconnection retaining uncertain execution state; and
- all existing quoting, selection, cancellation, output, traceback,
  IntelliSense, packaging, and workspace-cleanup contracts.

## Manual GUI verification

Use a disposable Rhino document and a Visual Studio experimental instance:

1. Install the v0.3.1 VSIX, start Rhino 8.35 or newer, and run
   `StartScriptServer`.
2. Run `examples/hello_rhino.py`. Confirm exactly one point, normal Output-pane
   completion, and that another run is immediately available afterward.
3. Run `examples/reliability/streaming_and_cancel.py`. Confirm live output,
   cooperative cancellation, and a successful subsequent run.
4. Run a script that continues for more than 15 seconds after its first output.
   Confirm it is not timed out; the 15-second interval applies only before the
   Rhino-side startup acknowledgment.
5. For a controlled delayed-start test, keep Rhino busy so ScriptServer queues
   the request. If startup occurs before 15 seconds, confirm normal completion.
   If it occurs later, confirm the startup-timeout diagnostic, the preserved
   workspace path, and rejection of a second run to that Rhino instance. Use
   **Cancel Running Script** before releasing Rhino; the late runner should
   acknowledge cancellation before entering user code.
6. For launch recovery, temporarily select a non-executable test file as the
   RhinoCode executable. Confirm the clear launch diagnostic, restore the real
   `%ProgramFiles%\Rhino 8\System\rhinocode.exe`, and confirm the next run is
   accepted without restarting Visual Studio.
7. With two Rhino instances, repeat the uncertain-start case for one instance
   and verify that no execution is sent accidentally to the other instance.

Do not deliberately interrupt a production Rhino session containing unsaved
work. Startup-timeout and disconnect tests intentionally exercise uncertain
state and should use disposable test instances.

## Remaining limitations and risks

- The startup timeout is configurable in code but has no persisted UI setting.
- After a startup timeout or uncertain disconnect, the in-memory execution slot
  remains blocked for the Visual Studio session even if a late runner later
  finishes. Restart Visual Studio only after confirming Rhino is idle; automatic
  late-result reconciliation is not implemented.
- Immediate nonzero-exit recovery relies on current RhinoCode exit semantics and
  the one-second classification window. A future CLI that reports failure after
  dispatching work could require a stronger transport-level acknowledgment.
- Cancellation remains cooperative. Native RhinoCommon/.NET calls cannot be
  forcibly interrupted safely, and `.py2` execution has no runner acknowledgment
  or bridge cancellation.
- Tracebacks remain text in the Output pane rather than clickable Error List
  entries.
