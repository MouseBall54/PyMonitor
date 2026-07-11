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
