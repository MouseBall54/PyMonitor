# Security decisions

- The agent connects only to numeric loopback host `127.0.0.1`.
- A cryptographically random 256-bit token is compared in constant time before
  any inspection method runs. Tokens are never logged.
- Pickle, eval, exec, callable execution, arbitrary repr/str/getattr/dir,
  descriptor invocation, and property reads are not used.
- User-defined objects expose type identity, address, base object size, their
  direct CPython instance dictionary when available, and static class members.
- NumPy is never imported by the agent. Its adapter activates only for an exact
  `numpy.ndarray` when a genuine NumPy module is already in `sys.modules`.
- Matplotlib and NumPy are never imported for Figure inspection. The adapter
  accepts only exact regular, already-loaded `Figure` or `Axes` objects and
  reads only a current, completed Agg C-extension RGBA buffer. It never calls
  target drawing, `buffer_rgba`, an Axes getter, artist/formatter code, target
  callbacks, descriptors, or properties. An Axes preview is explicitly the
  complete owning Figure. Stale, missing, non-Agg, inconsistent, or changing
  renders remain unavailable instead of executing target code. A successful
  preview is capped at 1024 by 1024 BGRA32 pixels (4 MiB).
- Object handles are opaque, session-scoped, TTL-bound, LRU-bounded, and all
  released on detach.
- Variable namespaces and object-child responses that carry handles are capped
  at 200 rows per request; the UI requests 100 at a time. Object Tree expansion
  is also bounded by ancestry validation, cycle detection, and a UI depth limit
  of eight.
- Module discovery snapshots `sys.modules` and accepts only exact already-loaded
  `ModuleType` objects. Namespace inspection reads the module's direct
  dictionary without importing modules or invoking module attributes.
- Thread discovery reads only CPython-owned private state from each exact
  instance dictionary and the interpreter's current-frame snapshot. It never
  calls overridable `Thread` properties or `is_alive()`.
- GC discovery runs only on explicit UI actions, never calls `gc.collect()`,
  and inspects at most 100,000 objects from the snapshot in the default UI.
  Filtering reads exact type metadata and addresses only; arbitrary previews
  are created only for the requested page.

## Embedded console namespaces

- Runtime-tree load performs a separate bounded console-owner detection scan of
  at most 100,000 objects. It never calls `gc.collect()` and periodic Variables
  refresh reuses the selected owner handle instead of repeating the GC scan.
- Detection reads only exact loaded type identities, safe type metadata, the
  direct CPython instance dictionary, and exact built-in dictionaries. IPython
  `_user_ns` is read as a backing field; its `user_ns` property is not invoked.
- Exact-string fields are found by iterating direct dictionaries and ignoring
  non-string target keys, so colliding key equality hooks are not invoked.
- GC snapshots use the genuine loaded CPython built-in and reject a replaced
  `gc.get_objects` callable instead of executing it.
- Custom automatic detection requires a console-like leaf class name and one of
  a fixed set of direct namespace fields. It does not infer arbitrary mappings
  from `__builtins__` or execute `getattr`, properties, descriptors, callables,
  `repr`, or `str`.
- `register_namespace` accepts only an exact dictionary, rejects duplicate
  identity, and retains at most 100 registrations. Registration intentionally
  keeps a strong reference until `unregister_namespace` is called.
- Console owner handles use the same opaque session, TTL, LRU, reference
  tracking, release and detach rules as inspected object handles.

## Deep object and class inspection

- Object Tree reads only exact built-in container entries or the direct
  CPython instance dictionary. It does not copy a complete list,
  mapping, or set before pagination.
- An identity already present in the selected ancestor chain is returned as a
  cycle marker and is not expanded. Each deeper request remains independently
  paginated and depth-validated.
- Class and inheritance data comes from direct class dictionaries, MRO entries,
  function code objects, and already materialized safe metadata. Properties,
  descriptors, callable wrappers, and annotation thunks are never invoked.
- Instance fields are read through CPython's generic dictionary accessor, not
  through a target-owned `__dict__` descriptor. This can materialize an empty
  instance dictionary but does not execute Python callbacks.
- Type and module metadata is accepted only when it is an exact string;
  hostile replacement values are reported as safe placeholders without
  formatting or coercion.
- Signature parameters and source locations are bounded. Unsafe default values
  and annotations are represented by placeholders instead of calling their
  formatting hooks.
- Pinned objects retain only session handles and navigation context. They are
  cleared on disconnect and do not grant access to a different process or a
  later authenticated session.

## Global runtime search

- Integrated search starts only on an explicit user action and breadth-first
  scans detected console namespaces, loaded module dictionaries and current frame namespaces without
  importing anything, then scans a bounded `gc.get_objects()` snapshot with
  the remaining request budget without forcing a collection.
- Recursive edges are limited to exact built-in containers and direct instance
  dictionaries. Class/member matches use static class dictionaries and code
  metadata; property getters, descriptors and user callables are never invoked.
- Aliased paths are still evaluated as locations, while a shared object's child
  graph is expanded only once per runtime root. Ancestor identities prevent
  cycles.
- Results, visited objects, children per object, class scans and depth all have
  hard bounds. Every response reports whether those bounds made the search
  incomplete, and only matching rows receive object handles.
- Root namespaces are consumed round-robin and the pending breadth-first
  frontier never exceeds the request object budget. Console discovery and the
  200-frame root cap are reported independently and participate in the overall
  completion status.
- The production Agent's 2,048-entry TTL/LRU handle store leaves room for a
  maximum 500-result search while the bounded frame, console and current UI
  source handles remain navigable.

## Quick Attach bootstrap

- CPython 3.10-3.13 Quick Attach copies one explicit Python line containing the
  bundled Agent path, loopback port, and a newly generated one-time token.
- The listener is active before the line is copied and accepts only the selected
  process PID. Once connected, the listener closes, so the clipboard token
  cannot establish a second connection.
- Quick Attach and Live Attach execute the shipped `bootstrap.py` freshly rather
  than importing a possibly cached bootstrap entry point. Before starting the
  Agent, it validates the package root's exact version, bootstrap ABI, and every
  cached module under one coherent package root. For a different source path, a
  bounded manifest and SHA-256 comparison must match every runtime `.py` file;
  the separately executed `bootstrap.py` and `managed_launch.py` entry points
  are excluded. Partial, mixed, or source-mismatched caches are rejected.
- A compatible bootstrap imports only the bundled Agent and starts its daemon
  connection thread. It does not evaluate a user expression or modify inspected
  values.
- Cache mismatch and active-connection failures use the one-time token to send
  `STALE_AGENT` or `ACTIVE_AGENT_CONFLICT` directly to the pending hello. A
  connected Agent returns `agentVersion` and `bootstrapAbi`; the controller
  reports an unexpected contract as `INCOMPATIBLE_AGENT` before inspection.
- An already-active Agent is reused only when host, port, token, and attach mode
  all match the repeated bootstrap. Any different connection setting is rejected
  as `ACTIVE_AGENT_CONFLICT`; the bootstrap never silently redirects an active
  Agent to a new controller.
- A stopped Agent package with matching version, ABI, runtime source manifest,
  and hashes can remain loaded from its original path even when the current
  PyMonitor uses another bundled path. This avoids unsafe module unloading while
  preventing a different runtime payload from crossing builds.

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

## GC-tracked objects

- The UI names the feature `GC-tracked objects`; it does not claim to enumerate
  every allocated Python or native object.
- Search does not call arbitrary `repr`, `str`, attribute access, descriptors,
  or properties. It matches type/module metadata and the displayed address.
- Each scan and response is bounded. Page handles use the existing session TTL
  and LRU store and are all released on detach.
- GC snapshots are observational and may include Inspector-owned runtime
  objects. No mutation, collection, referrer traversal, or reference retention
  analysis is performed.
