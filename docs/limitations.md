# Current limitations

- Windows x64 CPython 3.10-3.14 standard-GIL builds are the intended target;
  the Agent is exercised across every supported minor and Live Attach is
  integration-tested with a CPython 3.14 runtime.
- Unmodified attach to an already-running process requires CPython 3.14+ and
  remote debugging must not have been disabled by the target.
- Live attach can be delayed while the target is outside a Python safe
  execution point and the UI gives up waiting after 30 seconds.
- Ordinary inspector requests have a 15-second hard timeout. Canceling a UI
  selection stops that caller immediately while the matching response is
  drained; if the target never responds, PyMonitor aborts the connection and
  requires a new attach rather than leaving the UI request queue blocked.
- Snapshots are not stop-the-world and may be stale immediately after capture.
- Change highlighting compares one bounded scope snapshot with the preceding
  snapshot. Rebinding is reliable for the displayed name and identity token,
  while exact NumPy arrays and pandas DataFrames also contribute bounded content
  fingerprints for in-place mutation hints. Other mutable values are shown only
  when exposed preview, size, shape, dtype, or bounded metadata changes. Removed
  rows can be inferred only from a complete first-page snapshot. Highlights
  expire after the default 12-second window, which includes margin to keep the
  visible emphasis present for at least ten seconds.
- Safe summaries deliberately omit values for arbitrary user-defined objects.
- Object Tree requests 100 children at a time, stops UI expansion at depth 8,
  and does not traverse cycle markers. Opaque handles are session-scoped and
  can expire through the five-minute TTL or 512-entry LRU bound; refresh the
  source scope and reselect the current value after an expired state.
- Global Search is a bounded graph search, not a mathematical enumeration of
  every reference path. It evaluates aliases as separate result locations but
  expands a shared object's children once per runtime root. Defaults are 200
  results, 100,000 visited objects and depth 16, with 5,000 children per object;
  module/frame traversal receives a protected share of that budget and unused
  capacity flows to GC-tracked objects. The UI labels any limit-hit response as
  a bounded scan.
- Embedded-console discovery is same-process and scans at most 100,000
  GC-tracked owners on connection or explicit Runtime refresh. A console beyond
  that bound can be absent while the tree reports an incomplete scan. Periodic
  Variables refresh does not rescan GC. Arbitrary `exec` dictionaries have no
  reliable runtime marker and must be registered with `register_namespace`;
  registration keeps the exact dictionary alive until explicit unregister.
- Automatic custom-console detection requires a console-like leaf class name
  and a supported direct exact-dictionary field. Separate subprocesses,
  subinterpreters, mapping subclasses and property-only namespace APIs are not
  included.
- Frame and module namespace pages retain only the requested rows while
  scanning the direct dictionary. Pages follow dictionary insertion order
  rather than an alphabetical full-copy sort; target mutations can therefore
  shift subsequent page offsets. A key mutation during a scan is retried once;
  repeated mutation returns a retryable inspection error instead of applying a
  partial snapshot or producing false Removed rows.
- Pins and bounded navigation history protect their opaque handles from eager
  UI release for the current authenticated connection. Weak-referenceable
  objects can still disappear independently; non-weak-referenceable values can
  remain strongly reachable until unpin/history eviction, detach, or the Agent's
  five-minute TTL/512-entry LRU. Nothing reconnects to a later process session.
- Array previews support exact NumPy bool, integer, and floating-point 2D
  grayscale, 3D HWC/CHW color, and volume slices. Complex, structured, object,
  datetime, and string dtypes are metadata-only.
- DataFrame preview requires an exact pandas DataFrame from an already-loaded
  pandas module. The WPF table requests 50 rows by 20 columns; Agent responses
  are capped at 200 rows, 100 columns, and 2,000 cells. Unsupported extension
  cells are unavailable, and the bounded change fingerprint is not a full-frame
  checksum.
- Matplotlib preview requires an exact regular `Figure` or `Axes` from an
  already-loaded Matplotlib module and a current, completed Agg render. PyMonitor
  never calls `draw()` or `draw_idle()`; the target must call
  `fig.canvas.draw()` before refresh when a Figure is new or stale. An Axes
  selection shows the complete owning Figure. The preview is sampled to at most
  1024 by 1024 BGRA32 pixels (4 MiB), and the bounded change token is not a
  full-render checksum.
- CPython 3.10-3.13 has no supported unmodified live-injection API. Quick
  Attach reduces the cooperative bootstrap to one paste and Enter in the
  selected REPL, then validates the connected PID.
- CPython 3.14 Live Attach still needs a Python safe point. An idle interactive
  REPL may require one Enter keypress before the scheduled bootstrap runs.
- A byte-identical detached Agent package can be reused from another path when
  its version, bootstrap ABI, runtime source manifest, and hashes match. An
  incompatible, partial, or mixed package in `sys.modules` cannot be repaired by
  restarting PyMonitor alone. Quick Attach reports `STALE_AGENT` or
  `INCOMPATIBLE_AGENT`; fully stop and restart the debuggee, then attach again.
  `ACTIVE_AGENT_CONFLICT` means another Agent connection is still active with
  different settings; detach that session or restart the debuggee.
- Module browsing includes already-loaded exact Python modules only. Namespace
  snapshots can become stale immediately and modules removed during inspection
  return a structured invalid-argument error.
- GC browsing includes only objects returned by `gc.get_objects()`. Atomic
  values and types not tracked by Python's cyclic GC can be absent. The default
  WPF scan examines at most 100,000 entries and pages can change between scans.
- `gc.get_objects()` itself is synchronous inside the target and cannot be
  cancelled after it starts. The UI therefore never invokes it from the
  periodic refresh loop and does not force a collection.
- Managed stdout/stderr display is UTF-8 and line-oriented; binary stream output
  and partial lines are not rendered incrementally.
- `tracemalloc` covers only Python allocations made while tracing is active;
  native extension, memory-mapped, and GPU allocations are excluded.
- Execution monitoring requires CPython 3.12+. LINE and CALL events can have
  significant target overhead; use a path prefix and only necessary events.
- No GPU memory, subinterpreter, PyPy, free-threaded, embedded, x86, or ARM64
  support exists.
