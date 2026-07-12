# Quick Attach and idle REPL globals

Quick Attach is the default path for inspecting an already-running local
CPython process.

1. Refresh the Process list and select the Python process.
2. Click **Quick Attach**.
3. CPython 3.14+ uses `sys.remote_exec` automatically. If an interactive REPL
   is blocked waiting for input, press Enter once so it reaches a Python safe
   point.
4. CPython 3.10-3.13 has no supported unmodified live-injection API. Quick
   Attach starts the authenticated listener and copies one complete bootstrap
   line. Paste it into the selected REPL and press Enter once.
5. Inspect automatically opens `Modules / __main__` in Variables; no keep-alive
   loop or active user frame is required.

While CPython 3.10-3.13 is waiting for that manual paste, PyMonitor shows an
**awaiting bootstrap** instruction banner instead of treating the wait as a
normal loading operation. Paste into VS Code's **Debug Console** or the target
Python `>>>` REPL, not an integrated shell prompt. **Detach** cancels the pending
attempt. If no Agent connects within 120 seconds, the listener is closed and
Quick Attach can be run again.

## Plain `cmd.exe` Python REPL

Launching `python` in Command Prompt and assigning a variable, for example
`example_value = 1235`, does not start the PyMonitor Agent. The process can
appear in the Process list while its variables remain unavailable. Process
discovery is not an inspection connection.

Select that exact PID and run **Quick Attach**. On CPython 3.10-3.13, paste the
copied bootstrap at the Python `>>>` prompt, not at the `C:\>` command prompt,
and press Enter once. On CPython 3.14+, Quick Attach schedules the bootstrap
automatically; an idle REPL may need one blank Enter so it reaches a safe
execution point. Wait until PyMonitor reports Connected before selecting or
refreshing `Modules / __main__`.

Bindings created before the Agent starts are visible in the first authenticated
`__main__` snapshot. Bindings created or rebound afterward appear on Refresh or
the next automatic snapshot, where they can be classified and highlighted
against the preceding snapshot.

The copied line contains the bundled Agent directory, loopback port, and a new
one-time 256-bit token. The connected runtime PID must equal the selected PID.
It executes the bundled `bootstrap.py` freshly with `runpy.run_path`, then starts
the Agent daemon connection thread and returns control to the REPL immediately.
CPython 3.14+ Live Attach uses the same fresh bootstrap path.

Before importing or starting the Agent, the bootstrap snapshots the complete
`pyruntime_inspector_agent` prefix in `sys.modules`. It validates the cached
package root's Agent version, bootstrap ABI, and normalized `__init__.py` path,
requires every cached child module to live under that same shipped package, and
rejects a partial child-only cache. After import it repeats the path/tree check.
The authenticated `session.hello` then reports `agentVersion` and
`bootstrapAbi`, which the WPF client validates before requesting runtime data.

## Cached-Agent errors and recovery

Bootstrap compatibility failures are sent to the waiting listener immediately
instead of leaving PyMonitor in a loading state:

- `STALE_AGENT`: a cached package has a mismatched version, ABI, path, invalid
  module object, or partial/mixed module tree.
- `INCOMPATIBLE_AGENT`: the connected Agent's hello version or ABI differs from
  the WPF client's expected contract.
- `ACTIVE_AGENT_CONFLICT`: an Agent is already active with a different port,
  token, or attach mode.

For `STALE_AGENT` or `INCOMPATIBLE_AGENT`, fully stop and restart the Python
debuggee, then run Quick Attach again. Restarting PyMonitor alone cannot clear
modules cached inside the target process. For `ACTIVE_AGENT_CONFLICT`, first
Detach the existing PyMonitor session when possible; otherwise restart the
debuggee.

Module inspection reads the direct dictionary of an already-loaded exact
Python module. It does not import modules, invoke attributes, evaluate
properties, or call user-defined `repr`. Namespace results use the same safe
summaries, pagination, handle limits, and refresh cancellation as frame scopes.

The advanced **Listen** and **Live** buttons remain available for explicit
cooperative and CPython 3.14+ workflows.

## VS Code and debugpy breakpoints

For CPython 3.10-3.13, use this sequence with a paused VS Code debug session:

1. Start **Python Debugger: Current File** and stop at a breakpoint.
2. In PyMonitor, rescan processes, select that debuggee PID, and run **Quick
   Attach**.
3. Return to VS Code's **Debug Console**, paste the copied bootstrap line, and
   press Enter. Do not paste it into the integrated shell prompt.
4. Wait for Connected, then keep PyMonitor attached while continuing or
   stepping between breakpoints.

PyMonitor marks its read-only Agent transport as a debugger service thread, so
later breakpoints pause the application threads without pausing the inspector
connection.

Keep PyMonitor attached while stepping. Automatic snapshots update variable
rows in place without showing the loading overlay or resetting selection and
scroll position. A selected NumPy/OpenCV image, pandas DataFrame, or rendered
Matplotlib Figure/Axes is re-read in the background, so an in-place operation
such as `cv2.rectangle`, a DataFrame cell assignment, or a completed Figure
draw can be compared at the next paused line. The snapshot is still non-atomic;
the status timestamp identifies when the displayed data was read.

`samples/test_python_code.py` provides a practical OpenCV path. Stop before and
after its gradient creation, `cv2.rectangle`, `cv2.putText`, grayscale, and mask
lines; keep the corresponding array selected to observe each bounded preview
update. The sample also includes pandas values for checking the DataFrame table
and nested values for checking Object Tree breadcrumbs during the same session.
