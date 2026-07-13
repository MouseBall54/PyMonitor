# PyMonitor WPF interface

Build the Windows client from the repository root:

```powershell
& "$HOME\.dotnet10\dotnet.exe" build src\PyRuntimeInspector.App\PyRuntimeInspector.App.csproj -c Release
```

The public executable is `PyMonitor.exe`. The application is organized into
five top-level workspaces: **Inspect**, **Global Search**, **Launch**, **Memory**,
and **Events**.
The command bar keeps **Rescan**, **Quick Attach**, **Refresh**, **Detach**, and
**About** visible. Listener configuration, explicit Live Attach, environment
copying, automatic-refresh controls, and theme selection are in the Advanced
section.

## Selection-driven inspection

Inspect is a master-detail workflow with three linked regions:

1. Choose a frame scope, loaded module namespace, or GC-tracked object source
   in **Runtime Tree**.
2. Search, filter, and select a row in **Variables**.
3. Inspect that same selected object in **Overview**, **Object Tree**,
   **Class and Methods**, **DataFrame**, **Matplotlib**, or **Array and Image**.

Changing the variable selection replaces the complete Selected object context;
the detail views are not independent object pickers. Exact NumPy arrays enable
Array and Image, while an exact, already-loaded pandas DataFrame enables the
DataFrame table preview and an exact regular, already-loaded Matplotlib Figure
or Axes enables Matplotlib preview. Selecting one of these adapter values opens
its specialized tab automatically; ordinary values open Overview. Other values
keep specialized views unavailable while preserving Overview, Object Tree, and
Class and Methods.

The detail header exposes the selected path, type, safe preview, address, and
read-only status. A clickable root-to-current breadcrumb, depth label, and
history position keep the current location visible while drilling into nested
objects. Back, Forward, and Parent show their destination; any breadcrumb
ancestor can be opened directly. Pin/unpin, copy path, and copy address remain
available. Pins last for the current connection session and can be reopened
from **Pinned objects**.

The Variables **Name** column uses accent color and semibold weight so bindings
remain the primary scan target. Right-click any displayed text to copy that
single value. Right-clicking a table cell also selects the cell under the
pointer and offers the standard selected-cell copy command.

## Search, filters, and snapshot changes

Variables search matches name, type, module, qualified type, address, and safe
preview. Scope, change classification, and exact type filters combine with the
Arrays, Expandable, and Pinned toggles. **Clear filters** restores the complete
current page.

Overview has a separate search that filters only immediate-child names. Object
Tree has its own search over names already loaded into the lazy tree; it does
not fetch unexpanded pages. Matching ancestry expands temporarily and matching
names are emphasized. Clearing the query restores the expansion state that
existed before the search, including after a detail refresh.

For ordinary frame and module scopes, filtering operates on the current
snapshot. GC-tracked object search is different: the query is sent only after
an explicit **Search / Scan**, because each request can examine up to 100,000
GC-tracked objects. Periodic refresh deliberately skips GC scans.

Each ordinary scope snapshot is compared with its preceding snapshot. Changed
rows remain highlighted for a default 12-second window, ensuring at least ten
seconds of visible emphasis after UI rendering:

- **Added** means a name was not present in the preceding snapshot.
- **Removed** is a temporary ghost row for a binding that disappeared from a
  complete first-page snapshot.
- **Rebound** means the same name now points to a different object identity.
- **Updated** means identity is unchanged but bounded display metadata such as
  safe preview, size, shape, or dtype changed.

The first snapshot establishes the comparison baseline. **Reset comparison**
clears the baseline for the current scope. This is a bounded observation aid,
not a debugger watchpoint: arbitrary mutations inside a user object may not
change the exposed metadata and therefore may not be highlighted.

Automatic refresh reconciles Variables rows in place. It does not clear and
repopulate the table or show the loading overlay, so the selected row, realized
DataGrid containers, and scroll position remain stable. When the selected value
is a NumPy/OpenCV array, DataFrame, or rendered Matplotlib Figure/Axes, its
preview is refreshed in the background without resetting the current slice,
layout, row/column page, or last valid Figure image.

## Object Tree

Object Tree groups safe child values by their origin: collection items,
mapping values, instance fields, and direct instance-dictionary entries.
Children load on demand in pages of 100. **Load more** requests the next page
without materializing the entire container in the UI.

Opening a child makes it the Selected object, so its class, array, DataFrame,
Matplotlib, and overview information stay synchronized. The breadcrumb records
the complete root-to-current chain, highlights the current node, and lets the user return to
any ancestor without replaying Back. Back, Forward, and Parent remain available
with destination labels and tooltips. Repeated object identity in the current
ancestor chain is marked as a cycle and cannot be expanded. The UI stops
expansion at depth 8; the Agent also validates ancestry and request depth
independently.

The Selected object area distinguishes these states:

- **No selection**: select a Variables row to begin.
- **Loading**: bounded object and class requests are in progress.
- **Ready / empty**: data is available, or the value has no safely enumerable
  children.
- **Expired**: the session handle was evicted, reached its TTL, or its source
  binding disappeared; refresh the scope and select the current row again.
- **Error**: the request failed and diagnostics contain the reason.

## Class and Methods

The class tree separates class overview, base classes, MRO, instance fields,
instance methods, static methods, class methods, properties and descriptors,
class attributes, and inherited members. Function nodes can contain structured
parameter children with kind, bounded default preview, and safe annotation
text. Source file and first line are shown when code metadata is available.

Class search recursively matches the name, kind, declaring class,
signature/detail, source, and parameter text of the class details already
loaded for the current selection. Whitespace-separated terms are
case-insensitive AND terms within one item. A descendant match keeps and
expands its ancestor path; only the matching detail is emphasized and counted.
Clearing the query restores the expansion state from before the search. Search
does not issue another target request, so results remain limited to the bounded
class description currently loaded in the tree.

All class data is obtained through static class dictionaries and code-object
metadata. PyMonitor does not invoke properties, descriptors, annotation
thunks, wrapped user callables, or user-defined `repr` while building this
tree. Class members, scanned namespaces, parameters, and text lengths are
bounded.

## DataFrame

The DataFrame tab displays an index column plus dynamic data columns in a
virtualized, read-only table. Column headers include bounded names and dtypes.
The WPF page size is 50 rows by 20 columns, with independent row and column
navigation, shape/range text, refresh, empty/error states, and snapshot
consistency status.

The Agent accepts larger bounded pages but caps each response at 200 rows, 100
columns, and 2,000 cells. It does not import pandas, call DataFrame properties,
or serialize the complete frame. Unsupported extension-array cells remain
unavailable instead of invoking user accessors.

## Matplotlib

The Matplotlib tab accepts only exact regular `Figure` and `Axes` objects from
an already-loaded Matplotlib package. It reads only a current, completed render
from an Agg-derived canvas, samples it to at most 1024 by 1024, and renders a
maximum 4 MiB BGRA32 bitmap. PyMonitor never imports Matplotlib or NumPy for
this adapter and never calls `draw()`, `draw_idle()`, artist code, callbacks,
descriptors, or target properties.

An Axes selection deliberately displays its complete owning Figure and labels
that relationship. A new or stale Figure remains unavailable until target code
calls `fig.canvas.draw()` and the preview is refreshed. If the render buffer
changes during a bounded copy, the tab preserves the last valid image and can
retry on the next refresh.

## Connection modes

**Managed Launch** is the simplest way to inspect a script: choose the exact
Python executable and script in Launch, then start it. PyMonitor starts the
authenticated Agent before running the user script and preserves argv, working
directory, environment, stdout, stderr, and exit status.

**Quick Attach** connects to an existing local process. CPython 3.14+ uses Live
Attach and may require one Enter keypress when an idle REPL must reach a Python
safe point. CPython 3.10-3.13 requires one cooperative step: Quick Attach copies
a complete bootstrap line, which must be pasted at that Python REPL's `>>>`
prompt and executed once.

Starting plain `python` in `cmd.exe` and assigning a variable does not, by
itself, expose that process to PyMonitor. Seeing `python.exe` in the Process
list is only discovery. The Agent must first be started by Quick Attach (or by
the advanced listener bootstrap). After authentication, `Modules / __main__`
opens automatically and global REPL bindings are available without a keep-alive
loop.

For the advanced cooperative flow, click **Start listener**, use **Copy
environment**, apply the copied variables in another shell, and start a target
that loads the bundled Agent, for example:

```powershell
python samples\target_sample.py
```

## Appearance, persistence, and keyboard access

Light is the default theme; Dark remains optional. PyMonitor persists the
theme, refresh interval, window dimensions and maximized state, and left/right
pane widths in `%LOCALAPPDATA%\PyMonitor\settings.json`. Missing, malformed, or
out-of-range settings fall back to bounded defaults. About identifies
PyMonitor `26.7.11`, developer 박영문, the Windows x64 target, and the read-only
inspection model.

- `Ctrl+F` switches to Inspect and focuses Variables search.
- `F5` refreshes the active connection or selected scope.
- `Alt+Left` and `Alt+Right` navigate Selected object history.

The top status area keeps connection state, read-only mode, target PID, Python
version, architecture, private memory, and request latency visible without
requiring a separate detail tab.
