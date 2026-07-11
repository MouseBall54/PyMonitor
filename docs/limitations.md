# Current limitations

- Windows x64 CPython 3.10-3.14 standard-GIL builds are the intended target;
  core inspection is verified with CPython 3.12 and Live Attach with 3.14.6.
- Unmodified attach to an already-running process requires CPython 3.14+ and
  remote debugging must not have been disabled by the target.
- Live attach can be delayed while the target is outside a Python safe
  execution point and the UI gives up waiting after 30 seconds.
- Snapshots are not stop-the-world and may be stale immediately after capture.
- Mutable changes are not comprehensively detected.
- Safe summaries deliberately omit values for arbitrary user-defined objects.
- Array previews support exact NumPy bool, integer, and floating-point 2D
  grayscale, 3D HWC/CHW color, and volume slices. Complex, structured, object,
  datetime, and string dtypes are metadata-only.
- For CPython 3.10-3.13, the WPF process selector can only validate a
  cooperative connection's PID.
- Managed stdout/stderr display is UTF-8 and line-oriented; binary stream output
  and partial lines are not rendered incrementally.
- `tracemalloc` covers only Python allocations made while tracing is active;
  native extension, memory-mapped, and GPU allocations are excluded.
- Execution monitoring requires CPython 3.12+. LINE and CALL events can have
  significant target overhead; use a path prefix and only necessary events.
- No GPU memory, subinterpreter, PyPy, free-threaded, embedded, x86, or ARM64
  support exists.
