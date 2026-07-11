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
