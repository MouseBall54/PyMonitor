# Protocol 1.0

Every frame is a 4-byte unsigned big-endian JSON byte length, UTF-8 JSON, then
the exact number of optional payload bytes declared by `binaryLength`. JSON is
limited to 1 MiB and binary payloads to 8 MiB. Malformed frames close the
connection.

Requests contain `protocolVersion`, `messageType`, `requestId`, `method`,
`params`, and `binaryLength`. Responses echo the request ID and contain either
`ok: true` with `result`, or `ok: false` with a structured `error`.

The first request must be `session.hello` with the session token. A successful
hello returns `protocolVersion`, `agentVersion`, and integer `bootstrapAbi`.
The WPF client validates the Agent version and bootstrap ABI before sending any
inspection request and reports a mismatch as `INCOMPATIBLE_AGENT`. The supported
Phase 0 methods are `session.detach`, `runtime.getInfo`, `threads.list`,
`frames.list`, `scopes.list`, `objects.describe`, `objects.listChildren`,
`modules.list`, `modules.listNamespace`, `gc.listObjects`, `runtime.search`,
`objects.release`, `classes.describe`, `arrays.describe`, `arrays.preview`,
`arrays.tile`, `arrays.histogram`, `arrays.pixel`, `dataframes.describe`,
`dataframes.preview`, `figures.describe`, `figures.preview`, `memory.status`,
`memory.start`, `memory.stop`,
`memory.snapshot`, `memory.listSnapshots`, `memory.statistics`, and
`memory.diff`, plus `execution.status`, `execution.start`, `execution.stop`,
`execution.list`, and `execution.clear`. Collections accept zero-based `offset`
and `pageSize`. The default is 100. Scope, module-namespace, object-child, and
GC pages that create value handles are capped at 200 rows. Metadata-only module
listing and execution-event methods retain their method-specific limits up to
1,000 rows.

The fresh Quick/Live Attach bootstrap may answer the pending hello directly
with `STALE_AGENT` when the target has an incompatible or partial cached Agent
module tree, or `ACTIVE_AGENT_CONFLICT` when an Agent is already running with
different connection settings. These authenticated structured errors terminate
the attach attempt immediately instead of waiting for its normal timeout.

Requests are sequential on one TCP stream. Cancellation after a frame is sent
suppresses stale UI application but still drains that response before another
request can use the stream. A transport abort is reserved for disconnect and
the one-second cooperative detach deadline, and is also enforced after the
15-second hard request timeout. It unblocks any nonresponsive read, marks that
session disconnected, and makes queued requests fail deterministically.

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
user Python frame is active. Scope and module namespace results report
`ordering`, `scanComplete`, `totalIsExact`, and `mutationDetected`. A key
mutation is retried once; repeated mutation returns a structured retry error so
the client does not apply a partial total as a complete comparison snapshot.

`objects.listChildren` accepts `offset`, `pageSize`, the current navigation
`depth`, and up to 32 `ancestorIdentityTokens`. It reads only exact built-in
container entries or a statically available instance dictionary. Each child
reports its relation/path metadata, depth, cycle status, and whether it can be
expanded. The WPF client uses pages of 100 and applies a stricter depth limit of
eight for interactive navigation.

`classes.describe` returns bounded class references for bases, MRO, and
metaclass plus at most 200 effective members. Members identify their declaring
class and inherited state, classification, static source location, and a
structured function signature when it can be read without invoking user code.
Structured parameters include kind and bounded safe representations of
defaults and annotations.

`gc.listObjects` accepts `query`, `offset`, `pageSize`, and `maxObjects`. The
query is limited to 200 characters and matches only type name, module name,
qualified type name, or object address. `maxObjects` defaults to 100,000 and is
hard-capped at 1,000,000. Results report `trackedTotal`, `scannedCount`,
`truncated`, scan duration, and snapshot time. Only the returned page receives
object handles and safe previews. GC pages are capped at 200 rows so every
returned handle fits in the bounded session handle store.

`runtime.search` performs a read-only breadth-first integrated search beginning
at every loaded exact module namespace and current frame locals/globals/built-ins,
then uses the remaining scan budget for GC-tracked objects that may not be
reachable from those namespaces.
It matches whitespace-separated query terms across variable/object names, paths,
safe previews, type metadata and address, plus statically classified class,
method, property, descriptor and class-attribute metadata. Recursive traversal
uses only exact built-in containers and statically available instance dictionaries;
it does not call a property, arbitrary `repr`, `getattr`, `dir`, descriptor, or
user callable. Results report `kind`, `name`, the complete `location`, root source
metadata and an optional safe value handle so the WPF client can open the exact
object. The default bounds are 200 results, 100,000 visited objects and depth 16;
hard limits are 500, 200,000 and 32. `scanComplete` and the individual limit flags
make a bounded/incomplete result explicit.

Array preview and tile requests accept `normalization` (`AUTO`, `NONE`,
`MINMAX`, `PERCENTILE`, or `LABEL`) plus percentile bounds. Tiles require a
source `x`, `y`, `width`, and `height`; each dimension is capped at 1024.
Histogram bin counts are capped at 512 and source sampling at one million
elements. NaN and infinities in `arrays.pixel` use `{ "kind": "NaN" }`,
`+Infinity`, or `-Infinity` objects instead of non-standard JSON numbers.

`dataframes.describe` and `dataframes.preview` are available only for an exact,
already-loaded pandas `DataFrame`; the agent never imports pandas. The adapter
reads trusted internal C-extension descriptors directly and does not call
mutable pandas module functions, public DataFrame properties, user callables,
or scalar `repr`/`str`. Preview accepts `rowOffset`, `rowCount`, `columnOffset`,
and `columnCount`. Rows are capped at 200, columns at 100, and a combined page
at 2,000 cells so the complete table remains within the response budget. Results contain
bounded column names and dtypes, index labels, display-safe cell text, exact
page totals, independent row/column truncation flags, and snapshot mutation
status. Unsupported extension-array cells are returned as unavailable instead
of executing their accessors. Structural metadata and at most 64 uniformly
distributed safe cells contribute to the DataFrame change token; this is a
bounded change hint, not a full-frame checksum. No preview operation copies or
serializes the complete frame.

`figures.describe` and `figures.preview` are available only for exact regular,
already-loaded Matplotlib `Figure` and `Axes` objects. The Agent never imports
Matplotlib or NumPy for this adapter and never calls `draw`, `draw_idle`,
`buffer_rgba`, `Axes.get_figure`, an artist, formatter, callback, descriptor, or
target property. An `Axes` resolves through its static owning-Figure reference
and returns the complete Figure image with `sourceKind: Axes`,
`renderedKind: Figure`, and `axesUsesOwningFigure: true`.

Only a current, completed buffer owned by an Agg-derived canvas is readable.
Missing renders, stale Figures, non-Agg canvases, detached Axes, unsupported
renderer internals, and buffers that change during the bounded copy return a
successful response with `previewAvailable: false` and a structured
`availability` state, reason, message, and next action; the Agent does not draw
the target to make a preview available. `figures.preview` accepts `maxWidth`
and `maxHeight`, each capped at 1024. It samples the existing RGBA buffer
directly, revalidates renderer identity, stale state, shape, and sampled bytes,
and returns at most 1024 by 1024 BGRA32 pixels (4 MiB) with source dimensions
and sampling steps. A bounded pixel sample contributes to the Figure/Axes
change token; it is not a full-render checksum.

A successful Figure preview reports `snapshotConsistent: true`,
`sourcePixelFormat: RGBA32`, and payload `pixelFormat: BGRA32`. The payload
invariants are `stride == width * 4` and `binaryLength == stride * height`; the
WPF client rejects a response that violates them. A render that changes during
copy returns no binary payload and `snapshotConsistent: false` instead of a
partially mixed image.

Memory statistics support `lineno`, `filename`, and `traceback` grouping and a
maximum result limit of 200. Snapshot identifiers are opaque and scoped to the
agent session. At most eight snapshots are retained; an evicted identifier
returns a structured invalid-argument error.

Execution monitoring accepts selected event names, a buffer capacity from 100
to 10,000, and an optional path prefix. Event reads use `afterSequence` and a
maximum page size of 1,000. `MONITORING_UNAVAILABLE`,
`MONITORING_ALREADY_ACTIVE`, and `TOOL_ID_CONFLICT` are structured errors.
