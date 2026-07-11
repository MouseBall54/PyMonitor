# Security decisions

- The agent connects only to numeric loopback host `127.0.0.1`.
- A cryptographically random 256-bit token is compared in constant time before
  any inspection method runs. Tokens are never logged.
- Pickle, eval, exec, callable execution, arbitrary repr/str/getattr/dir,
  descriptor invocation, and property reads are not used.
- User-defined objects expose type identity, address, base object size, their
  static instance dictionary when safely available, and static class members.
- NumPy is never imported by the agent. Its adapter activates only for an exact
  `numpy.ndarray` when a genuine NumPy module is already in `sys.modules`.
- Object handles are opaque, session-scoped, TTL-bound, LRU-bounded, and all
  released on detach.

## CPython 3.14+ live attach

- Live attach uses the selected target's own interpreter to call
  `sys.remote_exec`, preventing a major/minor helper mismatch.
- The bootstrap contains the one-time session token, lives in a randomly named
  directory under the current user's temporary directory, and is deleted after
  connection or failure.
- Permission elevation is explicit and applies only to the short-lived helper
  through the Windows `runas` verb. The main Inspector UI remains non-elevated.
- Targets can disable the mechanism with `PYTHON_DISABLE_REMOTE_DEBUG=1`,
  `-X disable_remote_debug`, or a CPython build without remote-debug support.

## Managed launch

- Every managed start replaces the UI token with a fresh cryptographically
  random 256-bit token.
- The listener is started before the child process, and the connected runtime
  PID must match the process created by the launcher.
- The launcher uses `ProcessStartInfo.ArgumentList` with `UseShellExecute=false`;
  user arguments are not concatenated into a shell command.
- Inspector host, port, token, agent path, and unbuffered-output variables are
  applied after user overrides so the child cannot accidentally redirect the
  inspector connection.
- Tokens are not written to stdout, stderr, diagnostics, or process-output
  history. User-defined environment values remain visible in the launch editor
  because they are explicit launch configuration.

## Memory tracing

- `tracemalloc` is changed only through explicit UI commands.
- Tracing started by the Inspector is stopped and its snapshots are released on
  detach. Tracing already active before inspection is not stopped on detach.
- Snapshot storage is session-scoped and bounded to eight entries; statistics
  and diffs have a hard row limit.

## Advanced array transfer

- Preview and tile dimensions are each capped at 1024 pixels, keeping a BGRA
  payload below the protocol's 8 MiB binary limit.
- Histogram work is bounded to one million sampled elements and 512 bins.
- The agent copies only the selected sampled view or source tile immediately
  before rendering; it does not transfer or serialize the complete array.
- Only exact already-loaded NumPy `ndarray` objects are accepted. Object arrays
  and arbitrary `__array__` conversions are never evaluated.

## Execution monitoring

- Monitoring is off by default and starts only through an explicit command.
- Only tool IDs 3 and 4 are candidates; debugger, coverage, profiler, and
  optimizer IDs are never taken. A conflict is reported instead of replacing
  another tool.
- Callbacks do not retain or inspect return values, arguments, callables, or
  exception messages. They record only static code location and event metadata.
- Agent frames are excluded, the optional path prefix reduces unrelated events,
  and the ring buffer is capped at 10,000 entries.
- Detach disables callbacks, releases the tool ID, and clears retained events.
