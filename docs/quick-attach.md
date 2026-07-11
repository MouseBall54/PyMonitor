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
5. The Variables tab automatically opens `Modules / __main__`; no keep-alive
   loop or active user frame is required.

The copied line contains the bundled Agent directory, loopback port, and a new
one-time 256-bit token. The connected runtime PID must equal the selected PID.
It imports only the bundled `pyruntime_inspector_agent`, starts its daemon
connection thread, and returns control to the REPL immediately.

Module inspection reads the direct dictionary of an already-loaded exact
Python module. It does not import modules, invoke attributes, evaluate
properties, or call user-defined `repr`. Namespace results use the same safe
summaries, pagination, handle limits, and refresh cancellation as frame scopes.

The advanced **Listen** and **Live** buttons remain available for explicit
cooperative and CPython 3.14+ workflows.
