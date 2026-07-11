# Phase 6 execution monitoring

Phase 6 uses `sys.monitoring`, available in CPython 3.12 and newer, to collect a
bounded stream of execution locations.

## Events and safety

The UI can select PY_START, PY_RETURN, PY_YIELD, PY_UNWIND, RAISE, LINE, and
CALL. Callbacks record sequence, timestamp, thread ID, event name, code name,
filename, line, and instruction offset. Return values, call arguments,
callables, exception instances, and exception messages are not retained or
rendered. RAISE/PY_UNWIND may record only the exception type name.

## Coexistence and bounds

The agent tries tool ID 3 and then 4. It never claims the predefined debugger,
coverage, profiler, or optimizer IDs, and reports `TOOL_ID_CONFLICT` if both
candidates are occupied. The ring buffer is configurable from 100 to 10,000
entries, counts evictions, and supports incremental reads of at most 1,000
events. Agent code is excluded and an optional path prefix can limit monitoring
to the user's source tree.

Python 3.10 and 3.11 continue to support all non-monitoring inspector features;
`execution.start` returns `MONITORING_UNAVAILABLE` on those runtimes.
