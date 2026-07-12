"""Cooperative, read-only CPython inspection agent."""

from .server import start_inspector

__all__ = ["start_inspector"]
__version__ = "26.7.11"
