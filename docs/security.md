# Security decisions

- The agent connects only to numeric loopback host `127.0.0.1`.
- A cryptographically random 256-bit token is compared in constant time before
  any inspection method runs. Tokens are never logged.
- Pickle, eval, exec, callable execution, arbitrary repr/str/getattr/dir,
  descriptor invocation, and property reads are not used.
- User-defined objects expose type identity, address, base object size, their
  static instance dictionary when safely available, and static class members.
- NumPy is never imported by the agent. Its adapter activates only for an exact
  `numpy.ndarray` when a genuine NumPy module is already in `sys.modules`.
- Object handles are opaque, session-scoped, TTL-bound, LRU-bounded, and all
  released on detach.

## Managed launch

- Every managed start replaces the UI token with a fresh cryptographically
  random 256-bit token.
- The listener is started before the child process, and the connected runtime
  PID must match the process created by the launcher.
- The launcher uses `ProcessStartInfo.ArgumentList` with `UseShellExecute=false`;
  user arguments are not concatenated into a shell command.
- Inspector host, port, token, agent path, and unbuffered-output variables are
  applied after user overrides so the child cannot accidentally redirect the
  inspector connection.
- Tokens are not written to stdout, stderr, diagnostics, or process-output
  history. User-defined environment values remain visible in the launch editor
  because they are explicit launch configuration.
