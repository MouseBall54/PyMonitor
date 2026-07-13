"""Cooperative, read-only CPython inspection agent."""

from .console_namespaces import register_namespace, unregister_namespace
from .server import BOOTSTRAP_ABI, ActiveAgentConflictError, start_inspector

__all__ = [
    "ActiveAgentConflictError",
    "register_namespace",
    "start_inspector",
    "unregister_namespace",
]
__version__ = "26.7.13"
__bootstrap_abi__ = BOOTSTRAP_ABI
