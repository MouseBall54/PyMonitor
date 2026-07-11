# Protocol 1.0

Every frame is a 4-byte unsigned big-endian JSON byte length, UTF-8 JSON, then
the exact number of optional payload bytes declared by `binaryLength`. JSON is
limited to 1 MiB and binary payloads to 8 MiB. Malformed frames close the
connection.

Requests contain `protocolVersion`, `messageType`, `requestId`, `method`,
`params`, and `binaryLength`. Responses echo the request ID and contain either
`ok: true` with `result`, or `ok: false` with a structured `error`.

The first request must be `session.hello` with the session token. The supported
Phase 0 methods are `session.detach`, `runtime.getInfo`, `threads.list`,
`frames.list`, `scopes.list`, `objects.describe`, `objects.listChildren`,
`modules.list`, `modules.listNamespace`,
`objects.release`, `classes.describe`, `arrays.describe`, `arrays.preview`,
`arrays.tile`, `arrays.histogram`, `arrays.pixel`, `memory.status`,
`memory.start`, `memory.stop`,
`memory.snapshot`, `memory.listSnapshots`, `memory.statistics`, and
`memory.diff`, plus `execution.status`, `execution.start`, `execution.stop`,
`execution.list`, and `execution.clear`. Collections accept zero-based `offset` and `pageSize` (default
100, maximum 1000).

For Phase 1, NumPy object summaries also include `shape`, `dtype`, and
`payloadSizeBytes`. `arrays.preview` accepts `layout`, `colorOrder`,
`enabledChannels`, `sliceAxis`, and `sliceIndex`; its metadata includes source
sampling steps so preview coordinates can be mapped back to exact array pixels.
Managed Launch reports `attachMode: managed` from `runtime.getInfo`; cooperative
connections continue to report `attachMode: cooperative`.
CPython 3.14+ Live Attach reports `attachMode: live`.

`modules.list` snapshots already-loaded exact Python modules without importing
anything. `modules.listNamespace` reads the selected module's direct
`__dict__`, uses the same safe summaries and pagination as frame scopes, and
therefore exposes idle REPL globals through the `__main__` module even when no
user Python frame is active.

Array preview and tile requests accept `normalization` (`AUTO`, `NONE`,
`MINMAX`, `PERCENTILE`, or `LABEL`) plus percentile bounds. Tiles require a
source `x`, `y`, `width`, and `height`; each dimension is capped at 1024.
Histogram bin counts are capped at 512 and source sampling at one million
elements. NaN and infinities in `arrays.pixel` use `{ "kind": "NaN" }`,
`+Infinity`, or `-Infinity` objects instead of non-standard JSON numbers.

Memory statistics support `lineno`, `filename`, and `traceback` grouping and a
maximum result limit of 200. Snapshot identifiers are opaque and scoped to the
agent session. At most eight snapshots are retained; an evicted identifier
returns a structured invalid-argument error.

Execution monitoring accepts selected event names, a buffer capacity from 100
to 10,000, and an optional path prefix. Event reads use `afterSequence` and a
maximum page size of 1,000. `MONITORING_UNAVAILABLE`,
`MONITORING_ALREADY_ACTIVE`, and `TOOL_ID_CONFLICT` are structured errors.
