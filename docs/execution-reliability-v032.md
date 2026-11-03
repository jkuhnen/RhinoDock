# Execution-state recovery for v0.3.2

Version 0.3.2 adds automatic recovery after a v0.3.1 startup timeout or
transport interruption. It does not weaken overlap prevention: the bridge
releases a Rhino execution slot only after reading a valid terminal result for
the exact outstanding execution.

## Execution identity and terminal evidence

Every CPython workspace has a random `sessionId` and the selected Rhino
`pipeId`. Both values are written to `session.txt`, embedded in the generated
runner, included in `started.json`, and included in the atomic `result.json`.
The coordinator retains the expected workspace path, session ID, and pipe ID
when execution becomes uncertain.

Reconciliation accepts `result.json` only when it:

- can be opened and deserialized as the expected result schema;
- contains the exact retained `sessionId`;
- contains the exact selected Rhino `pipeId`; and
- belongs to the retained workspace for the same in-memory execution object.

Missing, partially visible, malformed, or identity-mismatched artifacts do not
release the slot. Filesystem read failures are treated as races and retried.
Neither a zero CLI exit code nor `started.json` is terminal evidence.

The runner finalizes in this order:

1. user code exits by success, Python failure, or acknowledged cancellation;
2. `sys.settrace(None)` disables cooperative cancellation tracing;
3. the temporary script import path is removed;
4. captured stdout/stderr is flushed and closed;
5. the identity-bearing terminal payload is written to a temporary file,
   flushed, and `fsync` is called; and
6. `os.replace` atomically publishes `result.json`.

Consequently, a valid terminal result proves that the runner has left user
code. Success, failure, and cancellation are all terminal and safely release
the slot.

## State lifecycle

| State | Meaning | Run behavior | Cancellation behavior |
| --- | --- | --- | --- |
| Running | The command is actively waiting for its Rhino runner. | Reject another run as currently running. | Signal cooperative cancellation. |
| Startup not acknowledged | The 15-second startup interval expired without `started.json`. | Keep the exact Rhino slot blocked. | Write `cancel.requested` so a delayed runner stops before user code. |
| Uncertain | The CLI/connection ended without a terminal result. | Keep the exact Rhino slot blocked. | Preserve cooperative cancellation and continue reconciliation. |
| Terminal result verified | Identity-matched `result.json` proves completion. | Remove the coordinator entry and permit another run. | Report that completion already occurred; do not retain a cancellation marker. |

When an execution first becomes uncertain, one bounded monitor checks its
terminal artifact every 500 milliseconds for up to 10 minutes. It performs no
Visual Studio UI work while waiting. Package disposal cancels the monitor and
disposes coordinator resources.

If the bounded monitor ends without terminal evidence, the safety lock remains.
The next **Run Active Python Script** or **Cancel Running Script** command
rechecks the exact artifact on demand, so a later completion can still be
reconciled without permanent polling.

After a late terminal result, the Output pane reports whether the script
succeeded, failed, or acknowledged cancellation, then states that the execution
slot was released and is ready. Captured late stdout/stderr is also displayed.
The completed workspace is deleted only after its result has been parsed and
the coordinator entry has been removed.

## Cancellation races

Cancellation checks for a terminal result before signaling or writing a marker.
For an uncertain workspace it checks again immediately after marker creation.
If completion wins the race, the marker is removed and the slot is reconciled.
If cancellation wins while execution is still unresolved, the marker remains
valid for a delayed runner. Reconciliation and normal completion remove a
coordinator entry idempotently, so concurrent monitor/cancel paths cannot
release the wrong execution or dispose it twice.

## Automated verification

Run from the repository root:

```powershell
dotnet build RhinoVSBridge.sln -c Debug
dotnet run --project tests\RhinoVSBridge.CoreTests -c Debug --no-build
python -m unittest discover -s tests -p "test_*.py" -v
dotnet build RhinoVSBridge.sln -c Release
dotnet run --project tests\RhinoVSBridge.CoreTests -c Release --no-build
```

The v0.3.2 suite covers late success, late Python failure, automatic slot
release, cancellation before delayed startup, cancellation after completion,
missing and malformed results, identity mismatch, completion/cancellation
races, same-pipe safety locking, and isolation between two Rhino pipe IDs. All
v0.3.1 launch, timeout, streaming, cancellation, quoting, workspace, selection,
and IntelliSense tests remain present.

## Actual GUI verification

The v0.3.2 VSIX passed real-world testing with Visual Studio 2026 and Rhino
8.36. Normal Python execution, RhinoCommon access, live stdout/stderr,
cooperative cancellation, failed-process-launch recovery, and scripts running
longer than 15 seconds remained operational.

For the delayed-completion case, Rhino was deliberately blocked for about 60
seconds. The bridge reported the 15-second startup timeout, retained the exact
execution slot as uncertain, and rejected a second run without misreporting it
as an actively running script. When Rhino resumed, the delayed runner completed
and atomically published its identity-bearing terminal result. Visual Studio
reported the late completion, released the slot automatically, and accepted a
follow-up run without restarting the IDE. This verifies the recovery path that
remained intentionally conservative in v0.3.1.

## Manual GUI regression checklist

Repeat the following with Visual Studio 2026 and a disposable Rhino 8.36 test
instance for future execution-protocol changes:

1. Install the v0.3.2 VSIX, run `StartScriptServer`, and confirm a normal script,
   live output, cancellation, and a follow-up run still work.
2. Run a script for longer than 15 seconds after it starts. Confirm there is no
   execution-duration timeout.
3. Block Rhino for about 60 seconds, submit a script, and wait for the startup
   timeout. Confirm the Output pane says startup was not acknowledged and the
   execution state is uncertain. A second run to that instance must be rejected
   with the uncertain-state message, not “script is running.”
4. Release Rhino and let the delayed script finish. Confirm the Output pane
   reports completion after startup timeout, displays captured output, states
   that the slot was released, and accepts the next run without restarting
   Visual Studio.
5. Repeat with a delayed script that raises a Python exception. Confirm the
   traceback/failure is reported and the slot is still released after the
   terminal failure result.
6. Repeat the blocked-start case, request cancellation before releasing Rhino,
   and confirm the delayed runner acknowledges cancellation before entering
   user code. The slot should then release automatically.
7. After a late completion is reported, invoke **Cancel Running Script**.
   Confirm it reports no active execution and creates no new
   `cancel.requested` marker.
8. With two Rhino instances, make only one pipe uncertain. Confirm the other
   instance remains selectable and no result from it releases or executes work
   in the uncertain instance.

Do not perform delayed-start testing in a production Rhino session containing
unsaved work.

## Remaining limitations and risks

- Automatic polling is intentionally bounded to 10 minutes. Later results are
  reconciled on the next Run or Cancel command, not by permanent background
  polling.
- Reconciliation state is in memory. After Visual Studio restarts, old
  incomplete workspaces are reported but are not automatically reattached to a
  Rhino execution slot.
- A permanently missing, malformed, or identity-mismatched terminal artifact
  remains blocked for safety. Manual workspace removal is appropriate only
  after independently confirming Rhino is idle.
- The startup timeout is configurable in code but still has no persisted UI
  setting.
- Immediate process-failure recovery retains the v0.3.1 one-second
  classification assumption for RhinoCode exit semantics.
- Cancellation remains cooperative; blocking native calls can delay it, and
  `.py2` has no runner-based acknowledgment or reconciliation protocol.
- Tracebacks remain Output-pane text rather than clickable Error List entries.
