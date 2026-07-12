import os
import sys
import time

MANAGED_ARGV = list(sys.argv)
MANAGED_CWD = os.getcwd()
MANAGED_ENV = os.environ.get("PYMONITOR_TEST_ENV")
MANAGED_DONT_WRITE_BYTECODE = sys.dont_write_bytecode

print("managed-stdout", flush=True)
print("managed-stderr", file=sys.stderr, flush=True)

time.sleep(float(os.environ.get("PYMONITOR_TEST_WAIT", "10.0")))
raise SystemExit(int(os.environ.get("PYMONITOR_TEST_EXIT_CODE", "0")))
