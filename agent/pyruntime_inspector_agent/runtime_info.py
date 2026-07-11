import os
import platform
import struct
import sys
from datetime import datetime, timezone


def timestamp():
    return datetime.now(timezone.utc).isoformat()


def get_runtime_info(agent_version, attach_mode="cooperative"):
    implementation = sys.implementation
    gil_probe = getattr(sys, "_is_gil_enabled", None)
    return {
        "pid": os.getpid(),
        "parentPid": os.getppid() if hasattr(os, "getppid") else None,
        "version": sys.version,
        "versionInfo": list(sys.version_info[:5]),
        "implementationName": implementation.name,
        "implementationVersion": list(implementation.version[:5]),
        "executable": sys.executable,
        "prefix": sys.prefix,
        "basePrefix": sys.base_prefix,
        "execPrefix": sys.exec_prefix,
        "platform": sys.platform,
        "machine": platform.machine(),
        "pointerSize": struct.calcsize("P"),
        "processArchitecture": f"{struct.calcsize('P') * 8}-bit",
        "isVirtualEnvironment": sys.prefix != sys.base_prefix,
        "isGilEnabled": bool(gil_probe()) if gil_probe is not None else None,
        "currentWorkingDirectory": os.getcwd(),
        "argv": list(sys.argv),
        "agentVersion": agent_version,
        "protocolVersion": "1.0",
        "attachMode": attach_mode,
        "snapshotTimestamp": timestamp(),
    }
