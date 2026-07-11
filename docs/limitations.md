# Phase 0 limitations

- Windows x64 CPython 3.10-3.14 standard-GIL builds are the intended target;
  this POC is currently verified with the installed CPython 3.12 runtime.
- Attach is cooperative only. There is no injection or attach to an arbitrary
  running process.
- Snapshots are not stop-the-world and may be stale immediately after capture.
- Mutable changes are not comprehensively detected.
- Safe summaries deliberately omit values for arbitrary user-defined objects.
- Array previews support exact uint8 2D grayscale, 3D HWC/CHW color, and volume
  slices; they use bounded nearest-neighbor sampling and return Gray8, RGB24,
  or BGRA32.
- The WPF process selector validates a cooperative connection's PID. It cannot
  make an arbitrary already-running process load the agent.
- Managed stdout/stderr display is UTF-8 and line-oriented; binary stream output
  and partial lines are not rendered incrementally.
- No live attach, memory timeline, tracemalloc control, tiling, histogram, GPU memory,
  subinterpreter, PyPy, free-threaded, embedded, x86, or ARM64 support exists.
