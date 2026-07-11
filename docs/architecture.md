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

Win32 memory APIs, tracing, and native protocol reimplementations are later
phases.

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

## Phase 3 live attach

The WPF app starts its loopback listener before invoking a short-lived helper
with the selected target's own `python.exe`. The helper calls
`sys.remote_exec(pid, bootstrap.py)`, while the bootstrap path and one-time token
remain in a randomly named user temporary directory until the agent connects.
The bootstrap adds only the shipped agent directory to `sys.path` and starts the
agent with `attachMode: live`.

The helper can optionally run with the Windows `runas` verb. This elevates only
the helper, not the WPF process. The target must be CPython 3.14+ and must reach
a safe Python execution point before the bootstrap runs.

## Phase 4 memory and timeline

The WPF process service samples Working Set, Private Bytes, Virtual Size, and
Peak Working Set from Windows. The ViewModel keeps at most 300 timestamped OS
and Python allocation samples.

The agent exposes explicit `tracemalloc` controls and retains at most eight
session-scoped snapshots. Statistics and diffs are bounded to 200 rows per
request. Tracing started by the Inspector is stopped on detach; tracing that
was already active in the target is preserved.

## Phase 5 advanced arrays

Array selection is separated from bounded rendering. A 2D plane is selected
from GRAY, HWC, CHW, or a volume slice, then only a sampled preview or a source
tile up to 1024 by 1024 is copied. Bool and integer arrays, float16/32/64, and
uint8 color arrays are rendered to Gray8, RGB24, or BGRA32 with explicit NONE,
MINMAX, PERCENTILE, or LABEL normalization.

Histograms sample at most one million values and return at most 512 bins. Raw
pixel queries remain separate from display normalization and encode NaN and
infinities as structured JSON values.

## Phase 6 execution monitoring

On CPython 3.12+, the agent claims only monitoring tool ID 3 or 4 and refuses to
start if both are occupied. User-selected events are recorded into a deque of
100 to 10,000 entries. Callbacks store code location, event kind, timestamp,
and thread ID but never inspect return values, call arguments, callable values,
or exception messages.

An optional normalized path prefix limits target overhead and noise. Agent code
locations are always excluded. The WPF client requests events incrementally by
sequence number and mirrors the target's bounded capacity.
