"""Cooperative, read-only CPython inspection agent."""

from .server import BOOTSTRAP_ABI, ActiveAgentConflictError, start_inspector

__all__ = ["ActiveAgentConflictError", "start_inspector"]
__version__ = "26.7.11"
__bootstrap_abi__ = BOOTSTRAP_ABI
