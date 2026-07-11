# Phase 4 memory and timeline

Phase 4 separates operating-system process memory from Python allocator data.

## OS metrics

The WPF process service reads Working Set, Private Bytes, Virtual Size, and Peak
Working Set. A bounded timeline retains the latest 300 samples together with
the current and peak `tracemalloc` values.

## Python allocation tracing

The Memory tab can start tracing with a traceback depth from 1 to 25, stop it,
take snapshots, show current allocation statistics, and compare two snapshots.
The agent stores at most eight snapshots and returns at most 200 statistic rows.

If tracing was already enabled by the target, the Inspector reports that the
start time is unknown and does not stop target-owned tracing on detach. If the
Inspector starts tracing, detach stops it and releases its snapshots.

`tracemalloc` only observes Python allocator blocks created while tracing is
active. It does not measure native extension allocations, mapped files, GPU
memory, or the complete operating-system process footprint.
