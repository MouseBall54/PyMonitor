"""Interactive PyMonitor target covering deep objects, changes, arrays, and large scopes."""

import sys
import threading
import time
import types


class PipelineBase:
    category = "demo"

    def inherited_status(self) -> str:
        return "ready"


class Pipeline(PipelineBase):
    default_threshold = 0.5

    def __init__(self, name: str, threshold: float = 0.5):
        self.name = name
        self.threshold = threshold
        self.status = "starting"
        self.metrics = {"processed": 0, "errors": 0}

    @property
    def computed_status(self) -> str:
        raise RuntimeError("PyMonitor must not execute target properties")

    def process(self, values: list[int], limit: int = 10) -> dict[str, int]:
        return {"count": min(len(values), limit)}

    @staticmethod
    def normalize(value: float, minimum: float = 0.0, maximum: float = 1.0) -> float:
        return max(minimum, min(maximum, value))

    @classmethod
    def from_name(cls, name: str, *, threshold: float = 0.5) -> "Pipeline":
        return cls(name, threshold)


class RaisingDescriptor:
    def __get__(self, instance, owner=None):
        raise RuntimeError("PyMonitor must not execute target descriptors")

    def __set__(self, instance, value):
        raise RuntimeError("PyMonitor must not execute target descriptors")


class HostilePreview:
    dangerous = RaisingDescriptor()

    def __repr__(self) -> str:
        raise RuntimeError("PyMonitor must not execute target __repr__")


pipeline = Pipeline("sample")
numbers = [1, 2, 3, 4]
nested = {"pipeline": pipeline, "numbers": numbers, "settings": {"enabled": True}}
nested["cycle"] = nested
changing_counter = 0
rebound_value = {"generation": 0}
transient_value = {"state": "present"}
hostile_preview = HostilePreview()

# A separate loaded module provides a large namespace without pushing the core
# demo variables off the first __main__ page.
large_scope_module = types.ModuleType("pymonitor_large_scope")
large_scope_module.__dict__.update({f"bulk_value_{index:05d}": index for index in range(5_000)})
sys.modules[large_scope_module.__name__] = large_scope_module

try:
    import numpy as np
except ImportError:
    np = None

if np is not None:
    numpy_image = np.arange(256 * 384 * 3, dtype=np.uint8).reshape(256, 384, 3)
    large_array = np.arange(2048 * 2048, dtype=np.uint16).reshape(2048, 2048)
else:
    numpy_image = "NumPy is not installed; array demo unavailable"
    large_array = numpy_image


def worker_scope() -> None:
    worker_counter = 0
    worker_payload = {"values": list(range(205)), "owner": pipeline}
    worker_cycle = []
    worker_cycle.append(worker_cycle)
    while True:
        worker_counter += 1
        worker_payload["counter"] = worker_counter
        time.sleep(0.25)


threading.Thread(target=worker_scope, name="PyMonitor demo worker", daemon=True).start()

print("PyMonitor UX demo target is running.", flush=True)
while True:
    changing_counter += 1
    numbers[0] = changing_counter
    pipeline.status = "even" if changing_counter % 2 == 0 else "odd"
    pipeline.metrics["processed"] = changing_counter
    if changing_counter % 4 == 0:
        rebound_value = {"generation": changing_counter}
    if changing_counter % 8 == 0:
        transient_value = {"state": "present", "generation": changing_counter}
    elif changing_counter % 8 == 4:
        del transient_value
    time.sleep(1)
