# Phase 0 architecture

Phase 0 is a headless, read-only proof of concept. A C# controller owns a
`127.0.0.1` TCP listener and a random 256-bit token. A cooperative Python agent
runs on a daemon thread inside the target and connects back to that listener.
The controller sends requests; the agent interprets Python objects in-process.

The controller never parses CPython object memory. The agent exposes only
bounded, paginated snapshots and opaque session-scoped handles. Disconnecting
or `session.detach` stops the agent connection, not the target program.

The minimal projects are:

- `PyRuntimeInspector.Protocol`: framing and a sequential request client.
- `PyRuntimeInspector.Cli`: a small listener/controller for cooperative targets.
- `pyruntime_inspector_agent`: runtime, frame, scope, safe object, class, and
  NumPy inspection.

Managed launch, WPF, Win32 memory APIs, live attach, tracing, and native helpers
are later phases.

## Phase 1 WPF shell

`PyRuntimeInspector.App` is a self-contained `win-x64` WPF/MVVM client. It
reuses the Phase 0 protocol and accepts a cooperative agent connection on a
loopback listener. The selected process is optional PID validation; it does not
inject an agent into a running process.

The ViewModel performs only asynchronous requests. A generation counter plus a
linked cancellation token prevents an older scope or detail response from
overwriting a newer selection. A minimum one-second refresh loop polls only the
selected scope (or runtime status when no scope is selected). Connection loss
clears live collections without blocking the UI thread.

The array viewer creates a frozen `WriteableBitmap` from bounded Gray8, RGB24,
or BGRA32 payloads. WPF handles nearest-neighbor zoom; the overlay draws a pixel
grid at high magnification. Exact pixel values are requested separately using
source coordinates.

## Phase 2 managed launch

The WPF app starts the listener before creating a Python process. It then runs
the selected interpreter with `-m pyruntime_inspector_agent.managed_launch`,
passes a fresh session token and the agent directory through the environment,
and verifies that the connected runtime PID equals the launched PID.

The wrapper restores the user script's `sys.argv`, `sys.path[0]`, working
directory, and `__main__` semantics before calling `runpy.run_path`. The C#
launcher uses `ProcessStartInfo.ArgumentList`, redirects stdout and stderr into
separate asynchronous line pumps, and preserves the user script's exit code.
Detach closes only the inspector connection; Stop terminates the managed process
tree. Closing the WPF application also stops a managed target to avoid orphaned
redirected processes.
