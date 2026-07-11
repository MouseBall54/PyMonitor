# Phase 3 CPython 3.14+ live attach

Phase 3 connects to an already-running Windows x64 CPython 3.14+ process without
modifying or restarting the target program.

## Flow

1. The WPF app starts a loopback listener with a new 256-bit token.
2. It creates a bootstrap in a randomly named user temporary directory.
3. A helper runs with the selected target's own Python executable.
4. The helper calls `sys.remote_exec(target_pid, bootstrap_path)`.
5. At the target's next safe execution point, the bootstrap imports the shipped
   agent and connects it to the listener.
6. Once authenticated, the temporary bootstrap and helper result are deleted.

The helper returns structured error codes including `UNSUPPORTED_PYTHON`,
`PERMISSION_DENIED`, `TARGET_NOT_FOUND`, `REMOTE_DEBUG_DISABLED`,
`REMOTE_EXEC_FAILED`, and
`HELPER_ELEVATION_CANCELLED`. Optional elevation uses `runas` for the helper
only. Detaching closes the inspector agent session but does not stop the target.

Remote debugging disabled before scheduling is reported as
`REMOTE_DEBUG_DISABLED`. After `sys.remote_exec` accepts a request there is no
completion channel, so a target that does not reach a safe execution point
appears as an agent connection timeout.

## Verification

The integration test starts the official CPython 3.14.6 Windows x64 runtime,
attaches through the complete helper/bootstrap/session path, verifies the PID
and `attachMode: live`, detaches, and confirms that the target is still running.
