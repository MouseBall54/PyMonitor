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
`objects.release`, `classes.describe`, `arrays.describe`, `arrays.preview`, and
`arrays.pixel`. Collections accept zero-based `offset` and `pageSize` (default
100, maximum 1000).
