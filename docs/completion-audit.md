# PyMonitor 26.7.11 completion audit

This audit maps the product objective to evidence from the current `master`
worktree and the clean Windows release rebuilt on 2026-07-12. A requirement is
marked proven only when its test, runtime behavior, or packaged artifact was
inspected directly. Passing a related narrow test is not treated as proof of a
broader requirement.

## Requirement evidence

| Requirement | Status | Authoritative evidence |
| --- | --- | --- |
| PyMonitor name, developer 박영문, and version 26.7.11 are consistent | Proven | Python release metadata tests; .NET assembly metadata; About runtime capture; portable and MSI verifiers report PyMonitor / 박영문 / 26.7.11; bundled Agent reports 26.7.11. |
| Variables is the master view and one selection drives every inspector | Proven | `VariablesCenteredUxTests`; current Release WPF run selected `pipeline` and `numpy_image`, with the persistent path/type/address/change header and Overview, Object Tree, Class and Methods, and Array and Image updating from the same selection. |
| Class, instance, method, descriptor, inheritance, and parameter trees | Proven | Agent safe-object tests and App selection tests; current WPF run showed instance/static/class methods, properties/descriptors, class attributes, inherited members, bounded signatures, source location, and parameter child nodes without evaluating the throwing property. |
| Lazy object traversal, pagination, cycles, depth, expiration, and navigation | Proven | Agent pagination/TTL/LRU/cycle/depth tests and App tree tests; current WPF run navigated `pipeline → metrics`, displayed level 1, root-to-current breadcrumb, history 2/2, and named Back/Forward/Parent destinations; Alt+Left/Alt+Right returned to the correct object. |
| Variable search and composable filters | Proven | App search/filter tests cover name, type, module, qualified type, preview, address, scope, change, type, array, expandable, and pinned combinations; current WPF `Ctrl+F` search reduced 28 bindings to four `pipeline` matches without losing the selected inspector. Explicit GC scan remains separate. |
| Added, Removed, Rebound, Updated, reset baseline, and changed-only behavior | Proven | Snapshot comparison tests cover classification, ghost-row lifetime, reset, filtering, stale snapshots, and bounded array/DataFrame mutation tokens; the current live target visibly updated rows and timestamps during one-second auto refresh. |
| Auto refresh does not flash or reset the Variables/DataFrame view | Proven | App tests assert in-place collection reconciliation, stable selection/page, no loading state, and no collection reset; the current live WPF run retained the selected object and layout while values changed. |
| NumPy/OpenCV array and image inspection | Proven | Agent array/OpenCV tests and App selection tests; the current WPF run rendered a 256×384×3 uint8 HWC/RGB preview with shape, strides, normalization, channel, pixel, and histogram controls. The CPython 3.12 debugger record covers gradient, rectangle, text, grayscale, and mask stages. |
| pandas DataFrame bounded preview and independent row/column paging | Proven | Agent DataFrame safety, 2,000-cell bound, dtype/index, mutation token, and pagination tests; App 50-row/20-column table, empty/error/consistency, independent paging, and no-flash refresh tests; the recorded CPython 3.12 debugger run inspected the live table while paused. |
| Light professional shell, optional Dark theme, semantic states, and accessibility labels | Partially proven | Current 100% Release captures show readable Light hierarchy, selection, empty state, command bar, status bar, and automation names. WPF smoke tests fail on binding warnings and exercise a 960×540 logical viewport with 5,000 virtualized rows. Per-monitor-v2 metadata is verified. Physical 125%, 150%, and 200% display scaling is still a manual gate. |
| Safe Mode does not execute arbitrary target code | Proven | Agent tests cover hostile `__repr__`, property, descriptor, wrapped callable, deferred annotation, fake NumPy/pandas modules, bounded metadata, and no automatic `gc.collect`; protocol limits, timeouts, cancellation, and loopback authentication are covered separately. |
| CPython 3.10–3.14, subprocess integration, detach, and stability | Proven | Current five-runtime matrix: 80 tests on 3.10.18, 3.11.9, 3.12.11, 3.13.7, and 3.14.0rc2. Current .NET Release suites: Protocol 5, Integration 2, App 49. Current 60-second gate: 10 cycles, 6,481 requests, maximum working-set growth 1,568,768 bytes. Current Managed Launch UI run confirmed Detach leaves the target running and Stop terminates it. |
| Agent loading leaves portable/install directories clean | Proven | Quick, Live, and Managed attach regression tests assert that no `.pyc` or `__pycache__` is created and that `sys.dont_write_bytecode` is restored. A clean packaged `PyMonitor.exe` was then used for a real Managed Launch; the portable verifier still found zero development artifacts afterward. |
| Portable ZIP and MSI contents, metadata, icon, shortcut definition, and hashes | Proven | Clean release contains 442 files. Portable and MSI administrative-extraction verifiers passed. ZIP SHA-256 is `DBBC904F11ED386D8FE0A9BACEE47CD98019FD8CBD34325B7FD073EE31DACD6D`; MSI SHA-256 is `87CEE64E5516A8FE723794C116DEBD87E89CEC5EBD6C9CFAFC0DF1C89DC680FE`; both sidecars match. |
| Previous-version upgrade and clean uninstall | Pending manual gate | `Test-InstallerLifecycle.ps1` verifies old install, major upgrade, executable and shortcut, related product codes, registry markers, and complete uninstall cleanup. It requires an elevated Windows session. The 2026-07-12 elevation request was canceled before installation, so the machine was not changed and no lifecycle result is claimed. |
| Documentation, limitations, architecture, protocol, security, release instructions, and before/after captures | Proven | README and `docs/` cover the requested surfaces. `docs/assets/ux-audit/01-before.jpg` is the baseline; the final Light, About, Dark selection, class tree, and array captures are linked from `ux-verification.md`. |
| Authenticode release signing | Correctly deferred | No trusted certificate was supplied. The signing pipeline remains available, while the current EXE/MSI are intentionally and explicitly unsigned as required. |

## Current decision

The implementation, safety, automated quality gates, real runtime inspection,
portable archive, MSI metadata/extraction, documentation, and hashes are proven.
The product goal remains active until the elevated MSI lifecycle and physical
125%, 150%, and 200% display-scaling checks have direct evidence.
