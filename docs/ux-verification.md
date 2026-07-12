# PyMonitor UX verification checklist

Use this checklist for an interactive release candidate of PyMonitor `26.7.11`
on Windows 10 or 11 x64. Run the packaged `PyMonitor.exe` and a real local
CPython target together; do not substitute a ViewModel-only test for the items
that exercise selection, focus, scaling, or process attachment.

## Visual shell and settings

- [ ] First launch uses the Light theme and keeps primary text, selected rows,
  disabled controls, diagnostics, and table values readable at 100%, 125%, and
  150% Windows scaling.
- [x] The four top-level tabs are Inspect, Launch, Memory, and Events; primary
  connection actions remain visible while advanced controls stay grouped.
- [ ] Switching Light/Dark, changing refresh interval, resizing panes/window,
  maximizing, closing, and reopening restores the saved settings.
- [ ] A malformed `%LOCALAPPDATA%\PyMonitor\settings.json` falls back to usable
  defaults without preventing startup.
- [x] About shows PyMonitor, version `26.7.11`, developer 박영문, Windows x64,
  and the read-only inspection boundary.

## Real Python connection

- [ ] Start plain `python` from `cmd.exe`, declare `before_attach = 1235`, and
  confirm that process discovery alone does not expose variables.
- [ ] Run Quick Attach for the exact PID. On CPython 3.10-3.13, paste the copied
  line at `>>>`; on CPython 3.14+, allow Live Attach and enter one blank line if
  the REPL must reach a safe point.
- [ ] After Connected appears, `Modules / __main__` opens and includes
  `before_attach`; a later `after_attach = 5678` appears after Refresh.
- [ ] Detach leaves the externally started Python process alive. Managed Stop,
  by contrast, terminates only the process tree started from Launch.

## Inspect workflow

In the connected REPL, create representative values:

```python
class Demo:
    class_value = 7
    def method(self, value: int = 3, *, label: str = "x") -> int:
        return value
    @property
    def unsafe_property(self):
        raise RuntimeError("must not execute")

demo = Demo()
demo.field = {"values": list(range(205))}
cycle = []
cycle.append(cycle)
empty = []

import pandas as pd
sales = pd.DataFrame(
    {"region": ["Seoul", "Busan"], "amount": [1250, 980]},
    index=["A-01", "B-02"],
)
```

- [x] Selecting each Variables row updates one persistent Selected object
  context across Overview, Object Tree, Class and Methods, Array and Image, and
  DataFrame.
- [x] Search matches name/type/module/address/safe preview, and scope/change/type
  plus Arrays/Expandable/Pinned filters combine correctly. Clear filters
  restores the current page.
- [x] `demo` groups its instance field, methods, properties, class attributes,
  and inherited members; `method` exposes parameter kinds/defaults/annotations
  and source without evaluating `unsafe_property`.
- [ ] `demo.field["values"]` pages after 100 items through Load more. Back,
  Forward, Parent, copy path, and copy address preserve the expected context.
- [x] Drilling into a nested Object Tree keeps the root-to-current breadcrumb,
  current depth, and history position visible; clicking an ancestor returns
  directly to it and Back/Forward/Parent expose their destinations.
- [x] Selecting `sales` opens a virtualized index-plus-columns DataFrame table
  with readable names, dtypes, shape, index values, and current range.
- [ ] A DataFrame larger than 50 rows and 20 columns pages independently in both
  directions, and empty/error/consistency states explain the next action.
- [ ] `cycle` is marked as a cycle instead of expanding indefinitely; a nested
  acyclic structure stops at the documented UI depth limit.
- [ ] Pinning a root and nested value makes both reopenable from Pinned objects;
  disconnect clears session pins.
- [ ] No selection, loading, empty (`empty`), expired handle/removal, and request
  error states remain distinguishable and explain the next action.

## Snapshot comparison and keyboard

- [x] Background auto refresh updates Variables rows in place without a loading
  overlay, visible table flash, selection loss, or scroll reset. Selected
  NumPy/OpenCV and DataFrame previews retain their current layout/page.
- [ ] After a baseline snapshot, exercise an added binding, `del` removal,
  same-name rebinding, and an in-place list/array metadata change. Confirm the
  Added, Removed, Rebound, and Updated classifications and ten-second visual
  highlight lifetime.
- [ ] Reset comparison clears the current scope baseline, and Changed-only
  filtering does not treat the new baseline as a change.
- [ ] `Ctrl+F` selects Inspect and focuses Variables search; `F5` refreshes;
  `Alt+Left` and `Alt+Right` navigate object history without stealing normal
  text-editing input.

## Safety and release surface

- [ ] Objects with throwing `__repr__`, properties, descriptors, wrapped
  callables, or deferred annotations are inspected without invoking user code.
- [ ] Large containers remain paginated, object handles can surface an expired
  state cleanly, and GC scanning occurs only through an explicit Search / Scan
  or refresh action for that source.
- [ ] The portable artifact is
  `PyMonitor-26.7.11-win-x64.zip`; the installer is
  `PyMonitor-26.7.11-win-x64.msi`; installed shortcuts launch `PyMonitor.exe`
  and Windows Apps & Features identifies PyMonitor and developer 박영문.

## Execution record — 2026-07-11

### Baseline findings and design decisions

The original window was captured before the redesign in
[`01-before.jpg`](assets/ux-audit/01-before.jpg). The main UX problems were
that Variables, Object Inspector, Class & Methods, and Array/Image read as
unrelated workspaces; the current object context disappeared between tabs; dark
surfaces and default WPF selection colors hid table content; and empty,
disconnected, loading, and stale states did not provide a useful next action.

The redesign uses one selection model and a three-pane hierarchy: Runtime
navigation, Variables master list, and a persistent Selected object inspector.
Overview, Object Tree, Class and Methods, Array and Image, and DataFrame now
consume that same selection. Light is the default theme, Dark is optional,
semantic change colors always include text and glyphs, and unavailable detail
tabs are hidden or explained. The layouts retain bounded, read-only inspection:
no user property, descriptor, callable, or custom representation is evaluated
to populate the UI.

### Interactive evidence

The Release WPF application was run beside
`C:\Users\young\AppData\Local\Programs\Python\Python311\python.exe` with
[`samples/target_ux_demo.py`](../samples/target_ux_demo.py). Windows reported
96 DPI (100% scale) for this session.

- Managed Launch connected to CPython 3.11.9 and opened `Modules / __main__`.
- Searching for `pipeline` reduced 28 bindings to four matches. Selecting the
  instance kept its name, path, type, address, and current change state visible
  while Overview showed `name`, `threshold`, `status`, and `metrics`.
- Class and Methods separated instance fields, instance/static/class methods,
  properties/descriptors, class attributes, and inherited members. Tree and
  DataGrid selection remain readable in Dark mode.
- Searching for `numpy_image` exposed Array and Image only for the selected
  `ndarray`; the preview showed shape `(256, 384, 3)`, `uint8`, HWC/RGB controls,
  normalization, slicing, pixel, and histogram surfaces.
- Auto refresh visibly updated Updated rows and displayed a timestamp with
  “since refresh”. A no-result search displayed an actionable empty state.
- Detach returned the UI to Disconnected while the launched Python PID was still
  alive. Closing the application then allowed the managed target to terminate.
- Light and Dark themes, About, launch fields, search/filter controls, selected
  DataGrid rows, selected tree nodes, status bar, and array controls were
  visually inspected. The last saved preference was restored to Light.

Final captures:

- [`03-final-light-empty.jpg`](assets/ux-audit/03-final-light-empty.jpg) —
  Light shell and empty state
- [`04-final-about.jpg`](assets/ux-audit/04-final-about.jpg) — product About
- [`10-final-dark-selected.jpg`](assets/ux-audit/10-final-dark-selected.jpg)
  — selection-driven Overview with a visible changed row
- [`11-final-class-selected.jpg`](assets/ux-audit/11-final-class-selected.jpg)
  — readable selected class-tree group in Dark mode
- [`12-final-array.jpg`](assets/ux-audit/12-final-array.jpg) — NumPy image and
  array controls

### Automated release evidence

- Agent suite: 80 tests on each available CPython 3.10.18, 3.11.9, 3.12.11,
  3.13.7, and 3.14.0rc2 runtime; the three monitoring tests are expected skips
  on runtimes before Python 3.12.
- .NET Release suites: 55 tests total (Protocol 5, Integration 2, App 48), with
  warnings treated as errors. The WPF smoke test fails on binding warnings and
  checks DataGrid virtualization with 5,000 rows at 960×540 logical pixels.
- Stability gate: 60 seconds, 10 attach/detach cycles, 4,716 requests, and
  1,642,496 bytes of maximum working-set growth (well below the 192 MiB gate).
- Portable and MSI release inspectors verified product/file/agent version,
  442 packaged files, MSI manufacturer/product/upgrade metadata, code page 949,
  administrative extraction, manifest DPI awareness, and matching SHA-256
  sidecars.

### Follow-up breakpoint evidence — 2026-07-12

VS Code's Python debugger ran [`samples/test_python_code.py`](../samples/test_python_code.py)
with CPython 3.12 while the debuggee was attached to the Release WPF app. The
Quick Attach bootstrap was evaluated from a paused Debug Console, and the Agent
continued answering while application threads stopped at later breakpoints.

- DataFrame selection showed the index, dynamic columns, dtypes, and paged table
  preview. Nested values showed the current Object Tree breadcrumb, depth, and
  history position throughout drill-down and ancestor navigation.
- `bgr_gradient` was inspected after gradient creation; `cv_image_color` was
  kept selected across `cv2.rectangle` and `cv2.putText`; `cv_image_gray` and
  `cv_circle_mask` were inspected after their creation. Each paused stage
  produced the corresponding bounded preview.
- Background snapshots kept the Variables selection and scroll context without
  displaying the loading overlay or rebuilding the table.

### Remaining manual release gates

The current desktop session exercised 100% scaling. Per-monitor-v2 metadata and
the 960×540 automated viewport gate pass, but 125%, 150%, and 200% physical
display scaling remain unchecked above until they are exercised interactively.
The MSI install → upgrade → uninstall lifecycle also remains unchecked because
it requires an elevated Windows session. Final ZIP and MSI binaries are
intentionally unsigned until a trusted Authenticode certificate is supplied.

## Completion audit refresh — 2026-07-12

A clean final build was run again with `samples/target_ux_demo.py`. The current
session verified `Ctrl+F` search, a persistent `pipeline` selection, live Updated
rows, `pipeline → metrics` navigation, level/breadcrumb/history context,
Alt+Left/Alt+Right, method and parameter trees, and the NumPy image preview.
Detach left managed PID 58776 running; Stop then terminated it. During this run,
the portable directory was found to accumulate Agent bytecode caches. That
release defect was fixed for Cooperative, Live, and Managed Attach, covered by
new regression tests, and retested using the rebuilt packaged executable. The
post-run portable verifier found no `.pyc` or `__pycache__` files.

The elevated MSI lifecycle was started once on 2026-07-12, but the Windows
elevation prompt was canceled before installation. No product was installed or
removed, and this remains a manual release gate rather than a failed lifecycle.

Current automated evidence is 80 Agent tests on each CPython 3.10–3.14 runtime,
56 .NET tests (Protocol 5, Integration 2, App 49), and a 60-second stability run
with 10 cycles, 6,481 requests, and 1,568,768 bytes maximum working-set growth.
The requirement-by-requirement result and remaining direct-evidence gaps are in
[`completion-audit.md`](completion-audit.md).
