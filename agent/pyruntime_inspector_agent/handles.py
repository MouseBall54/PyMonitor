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
        self._lock = threading.RLock()

    def put(self, value):
        handle = f"{self._session}:{uuid.uuid4().hex}"
        try:
            stored = (True, weakref.ref(value))
        except TypeError:
            stored = (False, value)
        with self._lock:
            self._prune_locked()
            self._entries[handle] = (time.monotonic() + self._ttl_seconds, stored)
            while len(self._entries) > self._max_entries:
                self._entries.popitem(last=False)
        return handle

    def get(self, handle):
        if type(handle) is not str or not handle.startswith(self._session + ":"):
            raise ObjectExpiredError("The selected object is no longer available.")
        with self._lock:
            self._prune_locked()
            entry = self._entries.pop(handle, None)
            if entry is None:
                raise ObjectExpiredError("The selected object is no longer available.")
            expires_at, (is_weak, stored) = entry
            value = stored() if is_weak else stored
            if value is None or expires_at <= time.monotonic():
                raise ObjectExpiredError("The selected object is no longer available.")
            self._entries[handle] = (time.monotonic() + self._ttl_seconds, (is_weak, stored))
            return value

    def release(self, handle):
        with self._lock:
            return self._entries.pop(handle, None) is not None

    def clear(self):
        with self._lock:
            self._entries.clear()

    def __len__(self):
        with self._lock:
            self._prune_locked()
            return len(self._entries)

    def _prune_locked(self):
        now = time.monotonic()
        expired = []
        for handle, (expires_at, (is_weak, stored)) in self._entries.items():
            if expires_at <= now or (is_weak and stored() is None):
                expired.append(handle)
        for handle in expired:
            self._entries.pop(handle, None)
