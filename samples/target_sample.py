import time

import numpy as np

from pyruntime_inspector_agent import start_inspector

GLOBAL_NUMBER = 123
GLOBAL_IMAGE = np.zeros((480, 640, 3), dtype=np.uint8)
GLOBAL_IMAGE[100, 200] = [10, 20, 30]
GLOBAL_FLOAT_IMAGE = np.linspace(-1.0, 1.0, 100, dtype=np.float32).reshape(10, 10)
GLOBAL_FLOAT_IMAGE[0, 0] = np.nan
GLOBAL_FLOAT_IMAGE[0, 1] = np.inf
GLOBAL_LABELS = np.arange(100, dtype=np.int32).reshape(10, 10) % 7


class Detector:
    class_name = "sample"

    def __init__(self, threshold: float = 0.5):
        self.threshold = threshold

    @property
    def dangerous_property(self):
        raise RuntimeError("Inspector must not execute this property")

    def predict(self, image: np.ndarray) -> list:
        return []


detector = Detector()
start_inspector()


def worker():
    local_value = "frame-local"
    local_array = np.arange(100, dtype=np.uint8).reshape(10, 10)
    while True:
        time.sleep(0.1)


worker()
