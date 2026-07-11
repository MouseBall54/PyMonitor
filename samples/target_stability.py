import time

import numpy as np

from pyruntime_inspector_agent import start_inspector


LARGE_IMAGE = np.arange(4096 * 4096, dtype=np.uint16).reshape(4096, 4096)


class StabilityMarker:
    pass


GC_MARKER = StabilityMarker()
start_inspector()


def workload():
    total = 0
    for value in range(200):
        total += value
    return total


def worker():
    while True:
        workload()
        time.sleep(0.001)


worker()
