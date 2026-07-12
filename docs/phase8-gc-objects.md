# Phase 8 GC-tracked object explorer

Phase 8 adds an explicit, bounded view over objects returned by Python's cyclic
garbage collector. It is not an `All objects` view: atomic values and extension
types that do not participate in cyclic GC can be absent.

## Use

1. Connect to a target and select **GC-tracked objects** in the Runtime Tree.
2. Enter a type name, module name, qualified type name, or displayed address.
3. Click **Search / Scan**. Use Previous and Next for additional pages.
4. Select a row to open the shared Selected object context and its Overview,
   Object Tree, Class and Methods, and optional Array and Image views.

Selecting the node, searching, changing page, or explicitly refreshing takes a
new snapshot. The one-second scope refresh loop never scans the GC heap.

## Bounds and safety

- The WPF client examines at most 100,000 tracked objects per request.
- The protocol hard limit is 1,000,000 scanned objects and 200 rows per page.
- Search text is capped at 200 characters.
- Search reads exact type metadata and CPython virtual addresses only.
- Safe previews and opaque handles are created only for the returned page.
- The agent never calls `gc.collect()`, arbitrary `repr`, `getattr`, properties,
  descriptors, referrer traversal, or user callables.
- Handles remain session-scoped, TTL-bound, and LRU-bounded.

Results include total tracked and scanned counts, whether the scan limit was
reached, duration, and snapshot time. Because the target continues running,
objects and page ordering can change immediately after any response.
