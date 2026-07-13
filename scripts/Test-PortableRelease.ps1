[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [string]$ExpectedVersion = "26.7.13",
    [string]$ExpectedProductName = "PyMonitor",
    [string]$ExpectedCompanyName = "박영문",
    [string]$PythonExecutable = $(if ($env:PYTHON_EXECUTABLE) { $env:PYTHON_EXECUTABLE } else { "python" })
)

$ErrorActionPreference = "Stop"
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$requiredFiles = @(
    "$ExpectedProductName.exe",
    "$ExpectedProductName.dll",
    "agent\pyruntime_inspector_agent\__init__.py",
    "agent\pyruntime_inspector_agent\address_search.py",
    "agent\pyruntime_inspector_agent\server.py",
    "agent\pyruntime_inspector_agent\modules.py",
    "agent\pyruntime_inspector_agent\console_namespaces.py",
    "agent\pyruntime_inspector_agent\gc_objects.py",
    "agent\pyruntime_inspector_agent\runtime_search.py",
    "samples\target_sample.py",
    "samples\target_managed.py",
    "README.md",
    "docs\release.md",
    "docs\security.md",
    "docs\quick-attach.md",
    "docs\console-namespaces.md",
    "docs\phase8-gc-objects.md"
)

foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $releaseRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Portable release is missing required file: $relativePath"
    }
}

function Get-PythonFileMap([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Python Agent package directory is missing: $Root"
    }

    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Filter *.py) {
        $relativePath = [IO.Path]::GetRelativePath($Root, $file.FullName)
        $files.Add($relativePath, $file.FullName)
    }
    return $files
}

$sourceAgentRoot = Join-Path $repositoryRoot "agent\pyruntime_inspector_agent"
$releaseAgentRoot = Join-Path $releaseRoot "agent\pyruntime_inspector_agent"
$sourceAgentFiles = Get-PythonFileMap $sourceAgentRoot
$releaseAgentFiles = Get-PythonFileMap $releaseAgentRoot
if ($sourceAgentFiles.Count -eq 0) {
    throw "Repository Python Agent package contains no Python source files: $sourceAgentRoot"
}

$missingAgentFiles = @(
    $sourceAgentFiles.Keys |
        Where-Object { -not $releaseAgentFiles.ContainsKey($_) } |
        Sort-Object
)
$extraAgentFiles = @(
    $releaseAgentFiles.Keys |
        Where-Object { -not $sourceAgentFiles.ContainsKey($_) } |
        Sort-Object
)
if ($missingAgentFiles.Count -gt 0 -or $extraAgentFiles.Count -gt 0) {
    $differences = @()
    if ($missingAgentFiles.Count -gt 0) {
        $differences += "missing: $($missingAgentFiles -join ', ')"
    }
    if ($extraAgentFiles.Count -gt 0) {
        $differences += "extra: $($extraAgentFiles -join ', ')"
    }
    throw "Portable release bundled Agent file set does not match repository source ($($differences -join '; '))."
}

$mismatchedAgentFiles = @(
    foreach ($relativePath in $sourceAgentFiles.Keys) {
        $sourceHash = (Get-FileHash -LiteralPath $sourceAgentFiles[$relativePath] -Algorithm SHA256).Hash
        $releaseHash = (Get-FileHash -LiteralPath $releaseAgentFiles[$relativePath] -Algorithm SHA256).Hash
        if ($sourceHash -ne $releaseHash) {
            $relativePath
        }
    }
)
if ($mismatchedAgentFiles.Count -gt 0) {
    throw "Portable release bundled Agent SHA-256 does not match repository source: $($mismatchedAgentFiles -join ', ')"
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
    AgentFileCount = $sourceAgentFiles.Count
    FileCount = (Get-ChildItem -LiteralPath $releaseRoot -Recurse -File).Count
}
