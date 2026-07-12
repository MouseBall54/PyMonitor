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
- `pyruntime_inspector_agent`: runtime, frame, scope, safe object, class,
  NumPy, pandas, and Matplotlib inspection.

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

The protocol client serializes requests. After a request frame is sent, caller
cancellation returns immediately while an internal task drains the matching
response to preserve frame alignment. Every exchange also has a 15-second hard
timeout; an unresponsive transport is aborted and the UI transitions to a
diagnosable disconnected state. Detach separately allows one second for a
cooperative `session.detach` before aborting the transport, so a pending read
cannot block window shutdown.

The current Inspect workspace uses a selection-driven master-detail structure:
Runtime Tree chooses the data source, Variables owns search/filter/change
comparison, and one persistent Selected object context drives Overview, Object
Tree, Class and Methods, DataFrame, Matplotlib, and Array and Image. Selecting an
exact supported adapter automatically opens its specialized tab; ordinary
objects open Overview. The Variables Name column is visually emphasized.
Overview and Object Tree have independent name searches: Object Tree searches
only already-loaded nodes, temporarily expands matching ancestry, and restores
the pre-search expansion state when cleared. Displayed text and DataGrid cells
can be copied from their right-click menus. Object navigation keeps bounded
history and parent context, while cycle/depth markers prevent unbounded tree
expansion. Pins and history protect referenced handles from eager release for
that connection. Weak-referenceable targets can still disappear, while
non-weak-referenceable targets may remain strongly held until the reference is
evicted/unpinned, the session detaches, or the Agent TTL/LRU expires; this is not
durable cross-session retention.

The array viewer creates a frozen `WriteableBitmap` from bounded Gray8, RGB24,
or BGRA32 payloads. WPF handles nearest-neighbor zoom; the overlay draws a pixel
grid at high magnification. Exact pixel values are requested separately using
source coordinates.

The Matplotlib viewer accepts only exact regular, already-loaded `Figure` or
`Axes` objects. It samples an already completed, current Agg RGBA buffer and
returns at most 1024 by 1024 BGRA32 pixels (4 MiB); it never asks the target to
draw. Selecting an Axes deliberately shows its complete owning Figure rather
than a cropped axes image.

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

All attach paths load the bundled Agent without leaving `__pycache__` or `.pyc`
files in a portable or installed release. Cooperative and Live Attach temporarily
disable bytecode writes only around the Agent import and then restore the target's
setting. Managed Launch starts the Agent bootstrap with `-B`, then restores the
target script's environment-controlled `PYTHONDONTWRITEBYTECODE` behavior before
executing user code.

## Phase 3 live attach

The WPF app exposes Quick Attach as the default connection command. For
CPython 3.14+, it starts its loopback listener before invoking a short-lived helper
with the selected target's own `python.exe`. The helper calls
`sys.remote_exec(pid, bootstrap.py)`, while the bootstrap path and one-time token
remain in a randomly named user temporary directory until the agent connects.
The helper executes the shipped `bootstrap.py` as a fresh file, adds only the
shipped agent directory to `sys.path`, validates the cached package root's
version, bootstrap ABI, and normalized path plus every cached package module's
path, and starts the agent with `attachMode: live` only when that module tree is
compatible.

The helper can optionally run with the Windows `runas` verb. This elevates only
the helper, not the WPF process. The target must be CPython 3.14+ and must reach
a safe Python execution point before the bootstrap runs.

For CPython 3.10-3.13, Quick Attach starts the listener and places a complete
single-line cooperative bootstrap on the clipboard. That line uses the same
fresh, cache-aware bootstrap as Live Attach. The selected runtime PID is still
verified after authentication. Once connected, the client lists
already-loaded modules and automatically opens the direct `__main__` namespace;
this keeps idle REPL globals visible without requiring an active frame.

A stale or partial package cache is reported immediately as `STALE_AGENT`; an
already-active Agent with different connection settings reports
`ACTIVE_AGENT_CONFLICT`. The authenticated hello returns `agentVersion` and
`bootstrapAbi`, and the WPF client rejects a mismatch as `INCOMPATIBLE_AGENT`
before any runtime inspection. A stale or incompatible cache requires a full
Python debuggee restart, because restarting only the WPF process does not clear
the target's `sys.modules`.

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

## Phase 8 GC-tracked objects

The agent takes an explicit `gc.get_objects()` snapshot and examines at most
100,000 entries for each WPF request. Filtering uses only exact type metadata
and the CPython address; previews and opaque handles are created only for the
requested page. The scan never forces a collection.

The WPF Runtime Tree exposes a selectable `GC-tracked objects` node. Search and
pagination use `gc.listObjects`, while selecting a returned row reuses the
existing Object, Class, and Array inspectors. The periodic refresh loop skips
this node so a heap scan runs only after selection, Search / Scan, pagination,
or an explicit application refresh.
