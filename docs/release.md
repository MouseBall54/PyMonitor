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
uses version, ABI, a coherent cached package module tree, and a bounded runtime
source manifest with SHA-256 hashes to reject stale same-process reuse before
inspection begins. A detached cache from another path is reused only when that
runtime payload is byte-identical; `bootstrap.py` and `managed_launch.py` remain
fresh entry points and are excluded from the runtime manifest.
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

## In-app update contract

Official tagged GitHub builds receive the `owner/repository` value from
`GITHUB_REPOSITORY` as the application's `GitHubRepository` assembly metadata.
At startup the application checks GitHub's latest stable Release at most once
per 24 hours. The automatic path is quiet: no-update, offline, HTTP, and invalid
response outcomes do not interrupt inspection. **About > Check for updates** is
the explicit manual path and reports its status to the user. A local build with
empty repository metadata does not perform the automatic network check.

The repository named by `GitHubRepository` must expose public Releases to end
users. The updater intentionally has no embedded GitHub token and does not ask
users for source-repository credentials. The current workflow publishes to and
injects the same repository, so that repository must be public before end-user
distribution. A separate public release repository would require additional
cross-repository credentials and workflow support and is not part of this flow.

Finding a newer version never authorizes a download or install. After the user
reviews the version and approves the update, the updater accepts exactly one
`PyMonitor-<version>-win-x64.msi` and one matching
`PyMonitor-<version>-win-x64.msi.sha256` from that stable Release. It validates
the sidecar's exact filename and SHA-256, verifies that Windows trusts the MSI's
Authenticode signature, and only then requests UAC elevation to start the MSI
major upgrade. Missing, duplicated, oversized, renamed, hash-mismatched, or
untrusted assets fail closed and are never executed.

The updater deliberately installs an MSI even when the running copy came from
the Portable ZIP. The approval UI must therefore state that accepting an update
from a Portable copy changes the installation model to a machine-wide MSI. The
old Portable directory is not the update target and may be removed manually
after the installed Start Menu copy is confirmed. Users who want to remain
Portable should instead verify the new ZIP sidecar and extract the new ZIP into
a separate directory.

## Authenticode signing

Stable Release signing requires a trusted code-signing PFX. An internal
self-signed PFX is also accepted by the GitHub workflow, but its tagged build is
published as a pre-release and is not an in-app update source. Do not commit
either kind of PFX. To sign the application before ZIP creation and then sign
the MSI locally:

```powershell
$password = Read-Host 'PFX password' -AsSecureString
.\scripts\Build-Release.ps1 `
  -CertificatePath C:\secure\release-signing.pfx `
  -CertificatePassword $password
```

`Sign-Artifacts.ps1` uses the x64 Windows SDK `signtool.exe`, SHA-256 file and
timestamp digests, DigiCert's RFC 3161 timestamp endpoint, and verifies every
signature. For a self-signed PFX, the GitHub workflow temporarily adds only its
public certificate to the ephemeral hosted runner's `LocalMachine\Root` store
with non-interactive `Import-Certificate` so that `signtool verify /pa` can
validate the signature. Certificate preparation has a two-minute timeout, is
separate from the long build step, and an `always()` cleanup step removes the
certificate and decoded PFX. Ordinary CI artifacts are explicitly named
`unsigned`; only a trusted stable tagged workflow supplies assets consumed by
the in-app updater.

## GitHub release operator runbook

실제 배포 시 복사해서 실행할 수 있는 짧은 순서는
[내부 GitHub Release 체크리스트](internal-release-checklist.md)를 참고합니다.

### One-time repository configuration

1. Keep the least-privilege `permissions: contents: write` grant in
   `.github/workflows/release.yml`. The repository default may remain read-only;
   confirm only that an organization or enterprise policy does not block the
   workflow's explicit grant. The workflow token must be able to create a Release.
2. Add both Actions secrets:
   - `WINDOWS_CERTIFICATE_BASE64`: Base64 of a code-signing PFX. A self-signed
     PFX produces an internal pre-release; a trusted PFX produces a stable Release.
   - `WINDOWS_CERTIFICATE_PASSWORD`: the PFX password.
3. Protect the secrets and certificate outside the repository. Never commit a
   PFX, decoded certificate, password, or generated artifact.
4. Before distributing the first updater-enabled build, make this Release
   repository public. The current workflow does not publish across repositories;
   never work around private Release access by embedding a personal access token
   in the app.

### Prepare every stable version

Choose the stable version `X.Y.Z` and update every version-bearing source before
creating the tag:

- `Directory.Build.props`: `Version`, `AssemblyVersion`, `FileVersion`, and
  `InformationalVersion`.
- `agent/pyproject.toml`, `agent/pyruntime_inspector_agent/__init__.py`, and
  `agent/pyruntime_inspector_agent/server.py`.
- user-visible version text and artifact examples in `MainWindow.xaml`,
  `AboutWindow.xaml`, `Help/HelpCatalog.cs`, `README.md`, and release docs.
- verifier defaults in `scripts/Test-PortableRelease.ps1` and
  `scripts/Test-InstallerRelease.ps1`, plus expected versions in the Python and
  .NET test projects.

Review every remaining literal occurrence of the previous version; keep one
only when it is deliberately describing a historical artifact rather than the
new product identity:

```powershell
rg -n --fixed-strings '<previous-version>' `
  Directory.Build.props agent src scripts tests README.md docs
```

Run the release metadata tests after the edit; they are the required guard that
the .NET product version, Python package/Agent handshake, UI identity, README,
and public artifact names still agree. Also require the normal CI workflow to
be green before tagging.

Commit the complete version change, push it, create the exact annotated
`v<version>` tag on that commit, and push the tag:

```powershell
$version = '26.7.12'
git add --all
git commit -m "release: PyMonitor $version"
git push origin HEAD
git tag -a "v$version" -m "PyMonitor $version"
git push origin "v$version"
```

No manual ZIP, MSI, hash, GitHub Release, or repository-slug editing is needed.
The tag workflow verifies `v<version>` against `Directory.Build.props`, injects
the current `GITHUB_REPOSITORY` slug into assembly metadata, requires both
signing secrets, builds and tests, signs the EXE before ZIP creation, creates
the ZIP sidecar, builds and signs the MSI, rewrites and re-verifies its sidecar,
then creates a GitHub Release with generated notes. A detected self-signed PFX
creates a pre-release; a trusted PFX creates a non-draft stable Release.

The Release contains exactly these four public assets:

```text
PyMonitor-X.Y.Z-win-x64.zip
PyMonitor-X.Y.Z-win-x64.zip.sha256
PyMonitor-X.Y.Z-win-x64.msi
PyMonitor-X.Y.Z-win-x64.msi.sha256
```

The signed EXE is inside the ZIP and is not a fifth public asset. The same exact
four files are retained as the `PyMonitor-signed` workflow artifact.
`workflow_dispatch` may be used to build that signed Actions artifact, but it
does not create a GitHub Release because there is no pushed release tag.

### Publication failure conditions

The workflow fails and either publishes nothing or leaves a partial Release
that requires inspection when any of these gates fails:

- the pushed tag is not exactly `v` plus the version in
  `Directory.Build.props`, or version metadata/tests disagree;
- either signing secret is absent, malformed, or has the wrong password;
- build, automated tests, portable verification, Agent source/hash comparison,
  MSI metadata/extraction verification, signing, timestamping, or signature
  verification fails;
- any exact ZIP, MSI, or sidecar is absent, stale, misnamed, or fails its hash
  contract;
- the workflow token lacks `contents: write`, GitHub refuses the verified tag,
  or `gh release create` fails, including when a Release already exists for the
  tag.

Inspect and correct the first failing gate before rerunning. If a partial
Release already exists, inspect its assets and remove the incomplete Release
before rerunning the unchanged, verified tag workflow. Generated artifacts and
PFX files remain ignored by Git.
