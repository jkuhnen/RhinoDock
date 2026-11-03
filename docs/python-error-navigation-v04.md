# Python error navigation for v0.4

Version 0.4 publishes Rhino CPython traceback locations through Visual Studio's
native Error List. Script execution still uses the official RhinoCode CLI and
does not run under the Visual Studio debugger.

## Traceback parsing

**PythonTracebackParser** reads standard CPython frame lines in the form:

    File "C:\path\script.py", line 7, in <module>

The parser preserves the path exactly, including spaces, Unicode, brackets,
commas, ampersands, and other Windows filename characters. It records every
frame, selects the innermost user frame as the primary location, and publishes
the remaining user frames from inner to outer. Generated **bridge_runner.py**
frames and pseudo-files such as **<string>** remain visible in the original
Output-pane traceback but are not offered as user navigation targets.

Python line numbers are one-based. Error List tasks use the Visual Studio SDK's
zero-based line value, so Python line 7 is stored as line 6. Tracebacks do not
provide a reliable column; navigation uses column zero.

The final unindented exception line becomes the Error List description, prefixed
with **Rhino VS Bridge:**. Missing or malformed frames are ignored safely and
never change script completion or execution-slot cleanup.

## Error List ownership and navigation

The package creates one **ErrorListProvider** named **Rhino VS Bridge**. Each
external traceback frame becomes an **ErrorTask** owned by that provider.
Starting a newly accepted Rhino execution clears only this provider's tasks;
build, IntelliSense, and other extensions' entries are never modified.

Double-click navigation uses the supported **ErrorListProvider.Navigate** API
with Visual Studio's code logical view. The task carries the full source path
and zero-based line. Visual Studio can therefore open a Python file that is
outside the current solution and position the editor at the reported line.

Error List publication is deliberately non-fatal. If the IDE rejects a task or
navigation metadata, execution cleanup continues and a diagnostic is written to
the Output pane.

Late failures discovered by v0.3.2 execution reconciliation use the same parser
and Error List provider after their identity-matched terminal result is
verified.

## Output pane decision

The complete, original stdout/stderr and traceback remain in **Output > Rhino VS
Bridge**. The supported **IVsOutputWindowPane.OutputTaskItemString** API was
evaluated for Output-pane navigation, but it also creates legacy Task List/Error
List items whose lifetime is coupled to clearing the pane. Using it alongside a
dedicated **ErrorListProvider** would duplicate errors and complicate stale-entry
ownership. Version 0.4 therefore keeps the existing reliable live Output stream
and uses the native Error List as the single navigation surface. No UI parsing,
mouse hooks, or other fragile Output-window hacks are used.

## Automated verification

Run from the repository root:

~~~powershell
dotnet build RhinoVSBridge.sln -c Debug
dotnet run --project tests\RhinoVSBridge.CoreTests -c Debug --no-build
python -m unittest discover -s tests -p "test_*.py" -v
dotnet build RhinoVSBridge.sln -c Release
dotnet run --project tests\RhinoVSBridge.CoreTests -c Release --no-build
~~~

The v0.4 tests cover:

- spaces, Unicode, punctuation, and special characters in Windows paths;
- one-based Python to zero-based Visual Studio line conversion;
- multiple frames and innermost-frame selection;
- exclusion of the generated Rhino runner from user-facing locations;
- chained exceptions;
- pseudo-files, malformed traceback text, and missing frames;
- package ownership, stale-entry clearing, navigation wiring, and version
  metadata.

All v0.3.2 execution, recovery, cancellation, selection, quoting, workspace, and
IntelliSense tests remain present.

## Manual GUI verification

Use Visual Studio 2026 and a disposable Rhino 8.36 or newer test instance:

1. Install the v0.4 VSIX, start Rhino's **StartScriptServer**, and run a normal
   script. Confirm execution, live output, cancellation, and a follow-up run
   still work.
2. Run **examples\reliability\error_test.py** or another saved script that
   raises on a known line. Confirm the complete original traceback remains in
   **Output > Rhino VS Bridge**.
3. Open **View > Error List**. Confirm its description begins **Rhino VS
   Bridge:** and the File and Line columns identify the user script rather than
   **bridge_runner.py**.
4. Double-click the error. Confirm Visual Studio opens the Python file and
   places the caret on the exact raising line.
5. Repeat with a file outside the open solution under a path containing spaces,
   Unicode, brackets, commas, and ampersands.
6. Use a script that calls a failing helper module. Confirm the innermost helper
   frame appears first and the outer script frame is also navigable.
7. Run another script. Confirm old Rhino VS Bridge entries are removed without
   changing build or IntelliSense errors from other providers.
8. Repeat a delayed-start failure that completes after startup timeout. Confirm
   late reconciliation releases the execution slot and publishes its traceback.

## Remaining limitations and risks

- The Error List integration must still be exercised in the real Visual Studio
  2026 GUI; automated tests validate parsing and SDK wiring but cannot simulate
  the IDE's double-click interaction.
- Parsing targets conventional CPython traceback formatting. Custom exception
  formatters or arbitrary stderr without frame lines remain Output-only.
- Tracebacks provide line numbers but not precise source columns, so navigation
  starts at column zero.
- Output-pane traceback text itself is not clickable; Error List is the
  supported navigation surface.
- **.py2** execution still lacks the CPython runner's structured result and
  traceback guarantees.
