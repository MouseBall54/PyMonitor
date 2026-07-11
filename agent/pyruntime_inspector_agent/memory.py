import collections
import threading
import tracemalloc
import uuid

from .runtime_info import timestamp


_MAX_SNAPSHOTS = 8
_lock = threading.RLock()
_snapshots = collections.OrderedDict()
_started_at = None
_started_by_inspector = False


def status():
    with _lock:
        tracing = tracemalloc.is_tracing()
        current, peak = tracemalloc.get_traced_memory() if tracing else (0, 0)
        return {
            "tracing": tracing,
            "startedAt": _started_at,
            "startedByInspector": _started_by_inspector,
            "tracebackDepth": tracemalloc.get_traceback_limit() if tracing else 0,
            "currentBytes": current,
            "peakBytes": peak,
            "overheadBytes": tracemalloc.get_tracemalloc_memory() if tracing else 0,
            "snapshotCount": len(_snapshots),
            "snapshotLimit": _MAX_SNAPSHOTS,
            "snapshotTimestamp": timestamp(),
        }


def start(traceback_depth=1):
    global _started_at, _started_by_inspector
    if type(traceback_depth) is not int or not 1 <= traceback_depth <= 25:
        raise ValueError("tracebackDepth must be between 1 and 25.")
    with _lock:
        already_tracing = tracemalloc.is_tracing()
        if not already_tracing:
            tracemalloc.start(traceback_depth)
            _started_at = timestamp()
            _started_by_inspector = True
            _snapshots.clear()
        result = status()
        result["alreadyTracing"] = already_tracing
        return result


def stop():
    global _started_at, _started_by_inspector
    with _lock:
        was_tracing = tracemalloc.is_tracing()
        if was_tracing:
            tracemalloc.stop()
        _snapshots.clear()
        _started_at = None
        _started_by_inspector = False
        result = status()
        result["wasTracing"] = was_tracing
        return result


def take_snapshot(label=None):
    if label is not None and type(label) is not str:
        raise ValueError("label must be a string or null.")
    with _lock:
        _require_tracing()
        snapshot = tracemalloc.take_snapshot()
        snapshot_id = str(uuid.uuid4())
        created_at = timestamp()
        entry = {
            "snapshot": snapshot,
            "snapshotId": snapshot_id,
            "label": (label or "")[:120],
            "createdAt": created_at,
        }
        _snapshots[snapshot_id] = entry
        _snapshots.move_to_end(snapshot_id)
        while len(_snapshots) > _MAX_SNAPSHOTS:
            _snapshots.popitem(last=False)
        return _snapshot_summary(entry)


def list_snapshots():
    with _lock:
        return {
            "items": [_snapshot_summary(entry) for entry in _snapshots.values()],
            "limit": _MAX_SNAPSHOTS,
            "snapshotTimestamp": timestamp(),
        }


def statistics(limit=50, group_by="lineno"):
    limit = _validate_limit(limit)
    group_by = _validate_group_by(group_by)
    with _lock:
        _require_tracing()
        snapshot = tracemalloc.take_snapshot()
        items = [_statistic_row(item) for item in snapshot.statistics(group_by)[:limit]]
        return {
            "groupBy": group_by,
            "items": items,
            "limit": limit,
            "snapshotTimestamp": timestamp(),
        }


def diff(before_snapshot_id, after_snapshot_id, limit=50, group_by="lineno"):
    limit = _validate_limit(limit)
    group_by = _validate_group_by(group_by)
    with _lock:
        before = _get_snapshot(before_snapshot_id)
        after = _get_snapshot(after_snapshot_id)
        differences = after["snapshot"].compare_to(before["snapshot"], group_by)
        return {
            "beforeSnapshotId": before_snapshot_id,
            "afterSnapshotId": after_snapshot_id,
            "groupBy": group_by,
            "items": [_statistic_diff_row(item) for item in differences[:limit]],
            "limit": limit,
            "snapshotTimestamp": timestamp(),
        }


def cleanup():
    global _started_at, _started_by_inspector
    with _lock:
        if _started_by_inspector and tracemalloc.is_tracing():
            tracemalloc.stop()
        _snapshots.clear()
        _started_at = None
        _started_by_inspector = False


def _snapshot_summary(entry):
    snapshot = entry["snapshot"]
    return {
        "snapshotId": entry["snapshotId"],
        "label": entry["label"],
        "createdAt": entry["createdAt"],
        "traceCount": len(snapshot.traces),
        "totalBytes": sum(stat.size for stat in snapshot.statistics("filename")),
    }


def _statistic_row(statistic):
    frame = statistic.traceback[0] if statistic.traceback else None
    return {
        "filename": frame.filename if frame is not None else "<unknown>",
        "lineNumber": frame.lineno if frame is not None else 0,
        "sizeBytes": statistic.size,
        "count": statistic.count,
        "averageBytes": int(statistic.size / statistic.count) if statistic.count else 0,
    }


def _statistic_diff_row(statistic):
    row = _statistic_row(statistic)
    row["sizeDiffBytes"] = statistic.size_diff
    row["countDiff"] = statistic.count_diff
    return row


def _get_snapshot(snapshot_id):
    if type(snapshot_id) is not str:
        raise ValueError("snapshotId must be a string.")
    try:
        entry = _snapshots[snapshot_id]
    except KeyError:
        raise ValueError("The tracemalloc snapshot does not exist or has expired.") from None
    _snapshots.move_to_end(snapshot_id)
    return entry


def _require_tracing():
    if not tracemalloc.is_tracing():
        raise ValueError("tracemalloc is not running.")


def _validate_limit(limit):
    if type(limit) is not int or not 1 <= limit <= 200:
        raise ValueError("limit must be between 1 and 200.")
    return limit


def _validate_group_by(group_by):
    if group_by not in ("lineno", "filename", "traceback"):
        raise ValueError("groupBy must be lineno, filename, or traceback.")
    return group_by
