import argparse
import ctypes
import json
import os
import pathlib
import secrets
import socket
import subprocess
import sys
import time

from pyruntime_inspector_agent import __bootstrap_abi__ as BOOTSTRAP_ABI
from pyruntime_inspector_agent import __version__ as AGENT_VERSION
from pyruntime_inspector_agent.protocol import read_frame, write_frame


ROOT = pathlib.Path(__file__).resolve().parents[2]
TARGET = ROOT / "samples" / "target_stability.py"


class Client:
    def __init__(self, connection):
        self.connection = connection
        self.sequence = 0

    def request(self, method, params=None):
        self.sequence += 1
        request_id = f"stability-{self.sequence}"
        write_frame(
            self.connection,
            {
                "protocolVersion": "1.0",
                "messageType": "request",
                "requestId": request_id,
                "method": method,
                "params": params or {},
            },
        )
        header, binary = read_frame(self.connection)
        if header.get("requestId") != request_id:
            raise AssertionError("Response request ID did not match.")
        if not header.get("ok"):
            error = header.get("error", {})
            raise AssertionError(f"{method} failed: {error.get('code')}: {error.get('message')}")
        return header["result"], binary


def working_set_bytes(process_id):
    if os.name != "nt":
        return None

    class ProcessMemoryCounters(ctypes.Structure):
        _fields_ = [
            ("cb", ctypes.c_ulong),
            ("PageFaultCount", ctypes.c_ulong),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    query_information = 0x0400
    virtual_memory_read = 0x0010
    handle = ctypes.windll.kernel32.OpenProcess(
        query_information | virtual_memory_read, False, process_id
    )
    if not handle:
        raise ctypes.WinError()
    try:
        counters = ProcessMemoryCounters()
        counters.cb = ctypes.sizeof(counters)
        if not ctypes.windll.psapi.GetProcessMemoryInfo(
            handle, ctypes.byref(counters), counters.cb
        ):
            raise ctypes.WinError()
        return counters.WorkingSetSize
    finally:
        ctypes.windll.kernel32.CloseHandle(handle)


def find_large_array_handle(client):
    frames, _ = client.request("frames.list")
    worker_frame = next(item for item in frames["items"] if item["functionName"] == "worker")
    scope, _ = client.request(
        "scopes.list",
        {
            "frameHandle": worker_frame["frameHandle"],
            "scopeType": "globals",
            "pageSize": 200,
        },
    )
    return next(
        item["value"]["handleId"] for item in scope["items"] if item["name"] == "LARGE_IMAGE"
    )


def run_session(python_executable, duration_seconds, memory_growth_limit, exercise_heavy_paths):
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    listener.settimeout(20)
    token = secrets.token_hex(32)
    env = dict(os.environ)
    env.update(
        {
            "PYTHONPATH": str(ROOT / "agent"),
            "PY_INSPECTOR_HOST": "127.0.0.1",
            "PY_INSPECTOR_PORT": str(listener.getsockname()[1]),
            "PY_INSPECTOR_TOKEN": token,
        }
    )
    process = subprocess.Popen(
        [python_executable, str(TARGET)],
        cwd=ROOT,
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    connection = None
    try:
        connection, _ = listener.accept()
        connection.settimeout(20)
        client = Client(connection)
        hello, _ = client.request("session.hello", {"token": token})
        if hello["agentVersion"] != AGENT_VERSION:
            raise AssertionError(f"Unexpected Agent version: {hello['agentVersion']}")
        if hello["bootstrapAbi"] != BOOTSTRAP_ABI:
            raise AssertionError(f"Unexpected bootstrap ABI: {hello['bootstrapAbi']}")

        runtime, _ = client.request("runtime.getInfo")
        if runtime["implementationName"] != "cpython":
            raise AssertionError("Stability target is not CPython.")

        baseline = working_set_bytes(process.pid)
        request_count = 0
        monitoring_supported = runtime["versionInfo"][:2] >= [3, 12]

        if exercise_heavy_paths:
            handle = find_large_array_handle(client)
            preview, binary = client.request(
                "arrays.preview",
                {
                    "handleId": handle,
                    "maxWidth": 1024,
                    "maxHeight": 1024,
                    "layout": "GRAY",
                    "normalization": "MINMAX",
                },
            )
            if preview["width"] > 1024 or preview["height"] > 1024 or len(binary) > 1024 * 1024:
                raise AssertionError("Large-array preview exceeded its transfer bound.")

            client.request("memory.start", {"tracebackDepth": 1})
            for index in range(12):
                client.request("memory.snapshot", {"label": f"stability-{index}"})
            snapshots, _ = client.request("memory.listSnapshots")
            if len(snapshots["items"]) != 8:
                raise AssertionError("tracemalloc snapshots were not bounded to eight entries.")

            if monitoring_supported:
                client.request(
                    "execution.start",
                    {
                        "eventNames": ["LINE"],
                        "bufferCapacity": 100,
                        "includePathPrefix": str(ROOT / "samples"),
                    },
                )

        deadline = time.monotonic() + duration_seconds
        while time.monotonic() < deadline:
            if request_count % 100 == 0:
                gc_objects, _ = client.request(
                    "gc.listObjects",
                    {
                        "query": "__main__.StabilityMarker",
                        "pageSize": 10,
                        "maxObjects": 100000,
                    },
                )
                if not any(
                    item["value"]["qualifiedTypeName"] == "__main__.StabilityMarker"
                    for item in gc_objects["items"]
                ):
                    raise AssertionError("Repeated GC scans lost the stability marker.")
                if gc_objects["scannedCount"] > 100000:
                    raise AssertionError("GC scan exceeded its object bound.")
            elif request_count % 10 == 0:
                main_scope, _ = client.request(
                    "modules.listNamespace",
                    {"moduleName": "__main__", "pageSize": 100},
                )
                if not any(item["name"] == "LARGE_IMAGE" for item in main_scope["items"]):
                    raise AssertionError("Repeated __main__ refresh lost the stability target globals.")
            else:
                client.request("runtime.getInfo")
            request_count += 1

        if exercise_heavy_paths:
            client.request("memory.stop")
            if monitoring_supported:
                client.request("execution.stop")
                events, _ = client.request("execution.list", {"limit": 1000})
                if len(events["items"]) > 100 or events["droppedCount"] < 1:
                    raise AssertionError("Execution monitoring did not remain bounded under load.")

        time.sleep(0.25)
        final_working_set = working_set_bytes(process.pid)
        growth = None if baseline is None else final_working_set - baseline
        if growth is not None and growth > memory_growth_limit:
            raise AssertionError(
                f"Target working set grew by {growth} bytes; limit is {memory_growth_limit}."
            )

        client.request("session.detach")
        time.sleep(0.1)
        if process.poll() is not None:
            raise AssertionError("Target exited after inspector detach.")
        return {
            "pythonVersion": runtime["version"].splitlines()[0],
            "processId": process.pid,
            "requestCount": request_count,
            "workingSetGrowthBytes": growth,
            "monitoringExercised": exercise_heavy_paths and monitoring_supported,
        }
    finally:
        if connection is not None:
            connection.close()
        listener.close()
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        stderr = process.stderr.read() if process.stderr else ""
        if process.returncode not in (0, 1, 15) and stderr:
            print(stderr, file=sys.stderr)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--duration-seconds", type=float, default=60)
    parser.add_argument("--cycles", type=int, default=10)
    parser.add_argument("--memory-growth-limit-mb", type=int, default=192)
    args = parser.parse_args()
    if args.duration_seconds <= 0 or args.cycles < 1 or args.memory_growth_limit_mb < 1:
        parser.error("duration, cycles, and memory growth limit must be positive")

    results = []
    results.append(
        run_session(
            args.python,
            args.duration_seconds,
            args.memory_growth_limit_mb * 1024 * 1024,
            True,
        )
    )
    for _ in range(args.cycles - 1):
        results.append(
            run_session(
                args.python,
                0.05,
                args.memory_growth_limit_mb * 1024 * 1024,
                False,
            )
        )

    print(
        json.dumps(
            {
                "status": "passed",
                "durationSeconds": args.duration_seconds,
                "cycles": args.cycles,
                "totalRequests": sum(item["requestCount"] for item in results),
                "maximumWorkingSetGrowthBytes": max(
                    (item["workingSetGrowthBytes"] or 0) for item in results
                ),
                "sessions": results,
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
