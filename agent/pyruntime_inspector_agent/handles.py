import collections
import threading
import time
import uuid
import weakref


class ObjectExpiredError(Exception):
    pass


class HandleStore:
    def __init__(self, max_entries=512, ttl_seconds=300.0):
        if max_entries < 1 or ttl_seconds <= 0:
            raise ValueError("Handle store bounds must be positive.")
        self._max_entries = max_entries
        self._ttl_seconds = ttl_seconds
        self._session = uuid.uuid4().hex
        self._entries = collections.OrderedDict()
        self._handles_by_identity = {}
        self._lock = threading.RLock()

    def put(self, value):
        identity = id(value)
        with self._lock:
            self._prune_locked()
            existing_handle = self._handles_by_identity.get(identity)
            if existing_handle is not None:
                existing = self._entries.pop(existing_handle, None)
                if existing is not None:
                    _, _, is_weak, stored = existing
                    current = stored() if is_weak else stored
                    if current is value:
                        self._entries[existing_handle] = (
                            time.monotonic() + self._ttl_seconds,
                            identity,
                            is_weak,
                            stored,
                        )
                        return existing_handle
                self._handles_by_identity.pop(identity, None)

            handle = f"{self._session}:{uuid.uuid4().hex}"
            try:
                is_weak, stored = True, weakref.ref(value)
            except TypeError:
                is_weak, stored = False, value
            self._entries[handle] = (
                time.monotonic() + self._ttl_seconds,
                identity,
                is_weak,
                stored,
            )
            self._handles_by_identity[identity] = handle
            while len(self._entries) > self._max_entries:
                evicted_handle, (_, evicted_identity, _, _) = self._entries.popitem(last=False)
                if self._handles_by_identity.get(evicted_identity) == evicted_handle:
                    self._handles_by_identity.pop(evicted_identity, None)
        return handle

    def get(self, handle):
        if type(handle) is not str or not handle.startswith(self._session + ":"):
            raise ObjectExpiredError("The selected object is no longer available.")
        with self._lock:
            self._prune_locked()
            entry = self._entries.pop(handle, None)
            if entry is None:
                raise ObjectExpiredError("The selected object is no longer available.")
            expires_at, identity, is_weak, stored = entry
            value = stored() if is_weak else stored
            if value is None or expires_at <= time.monotonic():
                if self._handles_by_identity.get(identity) == handle:
                    self._handles_by_identity.pop(identity, None)
                raise ObjectExpiredError("The selected object is no longer available.")
            self._entries[handle] = (
                time.monotonic() + self._ttl_seconds,
                identity,
                is_weak,
                stored,
            )
            return value

    def release(self, handle):
        with self._lock:
            entry = self._entries.pop(handle, None)
            if entry is None:
                return False
            _, identity, _, _ = entry
            if self._handles_by_identity.get(identity) == handle:
                self._handles_by_identity.pop(identity, None)
            return True

    def clear(self):
        with self._lock:
            self._entries.clear()
            self._handles_by_identity.clear()

    def __len__(self):
        with self._lock:
            self._prune_locked()
            return len(self._entries)

    def _prune_locked(self):
        now = time.monotonic()
        expired = []
        for handle, (expires_at, identity, is_weak, stored) in self._entries.items():
            if expires_at <= now or (is_weak and stored() is None):
                expired.append((handle, identity))
        for handle, identity in expired:
            self._entries.pop(handle, None)
            if self._handles_by_identity.get(identity) == handle:
                self._handles_by_identity.pop(identity, None)
