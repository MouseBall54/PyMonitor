# PyMonitor release hardening

Phase 7 turns the Phase 0-6 implementation into reproducible Windows release
artifacts. PyMonitor is developed by 박영문. The product version is `26.7.11`
and is shared by the .NET assemblies, the Python package, and the runtime
handshake. The independent integer bootstrap ABI is also shared by the Python
Agent and WPF attach client.

## Portable release

Run from the repository root in PowerShell:

```powershell
$env:DOTNET_EXE = 'C:\path\to\dotnet.exe' # only when dotnet is not on PATH
.\scripts\Build-PortableRelease.ps1
```

The script runs Python and .NET tests, publishes a self-contained `win-x64`
application, bundles `agent/`, `samples/`, the README and docs, rejects PDB,
PYC and `__pycache__` files, creates a ZIP, and writes a SHA-256 sidecar.
Before accepting the portable directory, the verifier compares the complete
`agent/pyruntime_inspector_agent` Python source file set with the repository
and requires every corresponding file SHA-256 to match. A same-version folder
left by an earlier build therefore fails verification if an Agent module is
missing, extra, or stale; do not publish or test from an older same-named
directory after a newer release has been built elsewhere.
The extracted directory is intentionally self-contained; the Python Agent is
loaded from its sibling `agent` directory and is not installed globally.
Quick Attach, Live Attach, and Managed Launch do not write bytecode caches into
that bundled directory; the target program's original bytecode setting is
restored before normal execution continues.
The public artifacts are named `PyMonitor-26.7.11-win-x64.zip` and
`PyMonitor-26.7.11-win-x64.zip.sha256`; the application executable is
`PyMonitor.exe`.

## Icon pipeline

`Assets/app-icon.png` is the transparent high-resolution brand master used by
the in-app header and About surface with high-quality WPF scaling. Run
`uv run --with pillow -- python .\scripts\build_app_icon.py` to regenerate the
Windows icon. The deterministic builder crops excess transparent padding,
resizes in premultiplied alpha, applies small-size sharpening, and writes 32-bit
RGBA frames at 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, and 256 pixels.

The multi-frame ICO is embedded in the EXE through `ApplicationIcon`; Windows
selects the appropriate title-bar, Explorer, and taskbar frame for the current
DPI. The same ICO is the MSI Apps & Features icon and Start Menu shortcut icon.

## Compatibility matrix

CI runs all Python Agent tests on standard-GIL x64 CPython 3.10, 3.11, 3.12,
3.13 and 3.14. The same matrix can be run locally with uv:

```powershell
.\scripts\Test-PythonMatrix.ps1
```

Set `PYTHON_3_14_EXECUTABLE` (or the corresponding minor-version variable) to
force a particular interpreter path. Live Attach remains a CPython 3.14+
feature; older versions are covered for cooperative attach and Managed Launch.

Any attach/bootstrap or debugger-thread compatibility change that makes an
already-loaded Agent unsafe to reuse must increment `BOOTSTRAP_ABI` in the
Python Agent and `ExpectedBootstrapAbi` in the WPF client in the same commit.
Keep the release metadata test that enforces this equality. The fresh bootstrap
uses version, ABI, source path, and the complete cached package module tree to
reject stale same-process reuse before inspection begins.
Do not bump the ABI for a UI-only change or a backward-compatible endpoint
addition that leaves cached-Agent reuse, bootstrap arguments, debugger-thread
behavior, and hello capabilities compatible.

## Stability gate

```powershell
.\scripts\Invoke-StabilityTests.ps1 -DurationSeconds 60 -Cycles 10
```

The gate exercises a 4096 by 4096 NumPy array through the bounded preview,
fills the 100-entry execution-event ring, rotates twelve tracemalloc snapshots
through the eight-entry limit, maintains a request stream for the selected
duration, and repeats connection/detach cycles. Every detach must leave the
target alive. On Windows the target working-set increase must remain under the
configured 192 MiB ceiling.

## MSI

Build the portable directory first, then run:

```powershell
.\scripts\Build-Installer.ps1
```

WiX Toolset SDK 5.0.2 creates a per-machine x64 MSI with an embedded cabinet,
Add/Remove Programs icon, major-upgrade protection, and Start Menu shortcut.
The MSI and its SHA-256 sidecar are written below `artifacts/installer/`.
Their public names are `PyMonitor-26.7.11-win-x64.msi` and
`PyMonitor-26.7.11-win-x64.msi.sha256`. `Build-Installer.ps1` also verifies the
sidecar, MSI product metadata, and an administrative extraction. The verifier
can be run directly when troubleshooting packaging:

```powershell
.\scripts\Test-InstallerRelease.ps1 `
  -InstallerPath .\artifacts\installer\PyMonitor-26.7.11-win-x64.msi
```

Run the destructive lifecycle check only in an elevated PowerShell session on
a disposable or explicitly approved Windows machine. It refuses to start if
either test product is already installed, installs the previous MSI, upgrades
it in place, verifies the machine-wide files and shortcut, then uninstalls the
current product and checks that product registration, files, shortcuts, and the
installer-owned registry value are gone:

```powershell
.\scripts\Test-InstallerLifecycle.ps1 `
  -PreviousInstallerPath .\artifacts\phase8\installer\PyRuntimeInspector-0.1.0-win-x64.msi `
  -InstallerPath .\artifacts\installer\PyMonitor-26.7.11-win-x64.msi
```

The approved elevated install → upgrade → uninstall lifecycle completed
successfully. `artifacts/installer-lifecycle-result.json` records
`Succeeded=true`: the previous 0.1.0 product was installed, upgraded in place to
PyMonitor 26.7.11, the current shortcut was verified, and current-product
uninstall cleanup passed. The install, upgrade, and uninstall logs are retained
under `artifacts/installer-lifecycle/20260712-120424`. Metadata, hash, and
administrative extraction checks remain separate required package checks.

Run the [UX verification checklist](ux-verification.md) against both the
portable executable and the installed shortcut. In particular, confirm the
default Light theme, persisted settings, Quick Attach behavior in a real
`cmd.exe` Python REPL, selection-driven Inspect views, keyboard access, and the
PyMonitor/version/developer metadata in About and Windows Apps & Features.

## Authenticode signing

Release signing requires a trusted code-signing PFX. Do not commit it. To sign
the application before ZIP creation and then sign the MSI:

```powershell
$password = Read-Host 'PFX password' -AsSecureString
.\scripts\Build-Release.ps1 `
  -CertificatePath C:\secure\release-signing.pfx `
  -CertificatePassword $password
```

`Sign-Artifacts.ps1` uses the x64 Windows SDK `signtool.exe`, SHA-256 file and
timestamp digests, DigiCert's RFC 3161 timestamp endpoint, and verifies every
signature. The tagged GitHub workflow refuses to publish unless
`WINDOWS_CERTIFICATE_BASE64` and `WINDOWS_CERTIFICATE_PASSWORD` are configured.
Ordinary CI artifacts are explicitly named `unsigned`.

## CI release flow

- `.github/workflows/ci.yml` runs the five-version Python matrix, .NET tests,
  the 60-second stability gate, portable packaging, and MSI packaging.
- `.github/workflows/release.yml` runs on `v*` tags, repeats all tests, signs
  the EXE and MSI, and publishes the ZIP, MSI, and SHA-256 files.
- Generated artifacts and PFX files are ignored by Git.
