[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [string]$ExpectedVersion = "0.1.0",
    [string]$PythonExecutable = $(if ($env:PYTHON_EXECUTABLE) { $env:PYTHON_EXECUTABLE } else { "python" })
)

$ErrorActionPreference = "Stop"
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$requiredFiles = @(
    "PyRuntimeInspector.exe",
    "PyRuntimeInspector.dll",
    "agent\pyruntime_inspector_agent\__init__.py",
    "agent\pyruntime_inspector_agent\server.py",
    "agent\pyruntime_inspector_agent\modules.py",
    "samples\target_sample.py",
    "samples\target_managed.py",
    "README.md",
    "docs\security.md",
    "docs\quick-attach.md"
)

foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $releaseRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Portable release is missing required file: $relativePath"
    }
}

$exe = Get-Item -LiteralPath (Join-Path $releaseRoot "PyRuntimeInspector.exe")
if (-not $exe.VersionInfo.ProductVersion.StartsWith($ExpectedVersion, [StringComparison]::Ordinal)) {
    throw "Expected product version $ExpectedVersion, found $($exe.VersionInfo.ProductVersion)."
}

$agentRoot = Join-Path $releaseRoot "agent"
$priorPythonPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = $agentRoot
    $actualVersion = & $PythonExecutable -B -c "import pyruntime_inspector_agent; print(pyruntime_inspector_agent.__version__)"
    if ($LASTEXITCODE -ne 0) {
        throw "The bundled Python Agent could not be imported."
    }
    if ($actualVersion.Trim() -ne $ExpectedVersion) {
        throw "Expected bundled Agent version $ExpectedVersion, found $actualVersion."
    }
}
finally {
    $env:PYTHONPATH = $priorPythonPath
}

$unexpected = Get-ChildItem -LiteralPath $releaseRoot -Recurse -File |
    Where-Object { $_.Extension -in ".pdb", ".pyc" -or $_.FullName -match "__pycache__" }
if ($unexpected) {
    throw "Portable release contains development artifacts: $($unexpected.FullName -join ', ')"
}

[pscustomobject]@{
    ReleaseDirectory = $releaseRoot
    ProductVersion = $exe.VersionInfo.ProductVersion
    AgentVersion = $actualVersion.Trim()
    FileCount = (Get-ChildItem -LiteralPath $releaseRoot -Recurse -File).Count
}
