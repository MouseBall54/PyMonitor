[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [string]$ExpectedVersion = "26.7.11",
    [string]$ExpectedProductName = "PyMonitor",
    [string]$ExpectedCompanyName = "박영문",
    [string]$PythonExecutable = $(if ($env:PYTHON_EXECUTABLE) { $env:PYTHON_EXECUTABLE } else { "python" })
)

$ErrorActionPreference = "Stop"
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$requiredFiles = @(
    "$ExpectedProductName.exe",
    "$ExpectedProductName.dll",
    "agent\pyruntime_inspector_agent\__init__.py",
    "agent\pyruntime_inspector_agent\server.py",
    "agent\pyruntime_inspector_agent\modules.py",
    "agent\pyruntime_inspector_agent\gc_objects.py",
    "samples\target_sample.py",
    "samples\target_managed.py",
    "README.md",
    "docs\release.md",
    "docs\security.md",
    "docs\quick-attach.md",
    "docs\phase8-gc-objects.md"
)

foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $releaseRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Portable release is missing required file: $relativePath"
    }
}

$exe = Get-Item -LiteralPath (Join-Path $releaseRoot "$ExpectedProductName.exe")
$expectedFileVersion = "$ExpectedVersion.0"
if ($exe.VersionInfo.ProductVersion -ne $ExpectedVersion) {
    throw "Expected product version $ExpectedVersion, found $($exe.VersionInfo.ProductVersion)."
}
if ($exe.VersionInfo.FileVersion -ne $expectedFileVersion) {
    throw "Expected file version $expectedFileVersion, found $($exe.VersionInfo.FileVersion)."
}
if ($exe.VersionInfo.ProductName -ne $ExpectedProductName) {
    throw "Expected product name $ExpectedProductName, found $($exe.VersionInfo.ProductName)."
}
if ($exe.VersionInfo.CompanyName -ne $ExpectedCompanyName) {
    throw "Expected company name $ExpectedCompanyName, found $($exe.VersionInfo.CompanyName)."
}
if ($exe.VersionInfo.FileDescription -ne $ExpectedProductName) {
    throw "Expected file description $ExpectedProductName, found $($exe.VersionInfo.FileDescription)."
}
if ($exe.VersionInfo.OriginalFilename -ne "$ExpectedProductName.dll") {
    throw "Expected original filename $ExpectedProductName.dll, found $($exe.VersionInfo.OriginalFilename)."
}
if (-not $exe.VersionInfo.LegalCopyright.Contains($ExpectedCompanyName, [StringComparison]::Ordinal)) {
    throw "Expected copyright metadata to contain $ExpectedCompanyName."
}

Add-Type -AssemblyName System.Drawing.Common
$icon = [Drawing.Icon]::ExtractAssociatedIcon($exe.FullName)
try {
    if ($null -eq $icon) {
        throw "The executable does not contain an application icon."
    }
}
finally {
    if ($null -ne $icon) { $icon.Dispose() }
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
    FileVersion = $exe.VersionInfo.FileVersion
    ProductName = $exe.VersionInfo.ProductName
    CompanyName = $exe.VersionInfo.CompanyName
    AgentVersion = $actualVersion.Trim()
    FileCount = (Get-ChildItem -LiteralPath $releaseRoot -Recurse -File).Count
}
