"""Bootstrap an inspector agent, then execute a user script as ``__main__``."""

import os
import runpy
import sys

from .server import start_inspector


def main():
    if len(sys.argv) < 2:
        raise SystemExit("Usage: python -m pyruntime_inspector_agent.managed_launch SCRIPT [ARGS ...]")

    script = os.path.abspath(sys.argv[1])
    if not os.path.isfile(script):
        raise SystemExit(f"Python script not found: {script}")

    user_arguments = sys.argv[2:]
    sys.argv = [script, *user_arguments]
    script_directory = os.path.dirname(script)
    if sys.path:
        sys.path[0] = script_directory
    else:
        sys.path.insert(0, script_directory)

    start_inspector()
    runpy.run_path(script, run_name="__main__")


if __name__ == "__main__":
    main()
