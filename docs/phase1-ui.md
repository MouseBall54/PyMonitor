# Phase 1 WPF shell

Build the self-contained Windows client:

```powershell
& "$HOME\.dotnet10\dotnet.exe" build src\PyRuntimeInspector.App\PyRuntimeInspector.App.csproj -c Release
```

Run `src\PyRuntimeInspector.App\bin\Release\net10.0-windows\win-x64\PyRuntimeInspector.App.exe`.
Choose a port, keep the generated 256-bit token, and click **Attach**. While the
UI says `Waiting for cooperative target`, use **Copy environment** and apply the
copied variables in a second PowerShell session, then run a cooperative target:

```powershell
python samples\target_sample.py
```

Selecting a frame loads locals by default; expand it to choose globals or
built-ins. Selecting a variable loads static object and class details. Exact
NumPy arrays also enable the Array/Image tab.

Phase 1 originally left **Launch** disabled. The current Phase 2 app enables it
with interpreter, script, argv, cwd, environment, output, Stop, Restart, and
exit-code controls. Live attach remains Phase 3.
