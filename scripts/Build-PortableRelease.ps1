[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$SkipArchive
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { (Get-Command dotnet -ErrorAction Stop).Source }
$python = if ($env:PYTHON_EXECUTABLE) { $env:PYTHON_EXECUTABLE } else { (Get-Command python -ErrorAction Stop).Source }
[xml]$props = Get-Content -LiteralPath (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$artifactRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root "artifacts" }
$releaseName = "PyRuntimeInspector-$version-$RuntimeIdentifier"
$releaseDirectory = Join-Path $artifactRoot $releaseName
$archivePath = Join-Path $artifactRoot "$releaseName.zip"

function Assert-WorkspaceOutput([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $workspacePrefix = $root.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output must stay inside the workspace: $fullPath"
    }
}

Assert-WorkspaceOutput $artifactRoot
Assert-WorkspaceOutput $releaseDirectory
if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

if (-not $SkipTests) {
    $priorPythonPath = $env:PYTHONPATH
    try {
        $env:PYTHONPATH = Join-Path $root "agent"
        & $python -m unittest discover -s (Join-Path $root "tests\agent_tests") -v
        if ($LASTEXITCODE -ne 0) { throw "Python tests failed." }
    }
    finally {
        $env:PYTHONPATH = $priorPythonPath
    }

    $env:PYTHON_EXECUTABLE = $python
    & $dotnet test (Join-Path $root "PyRuntimeInspector.slnx") -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw ".NET tests failed." }
}

& $dotnet publish (Join-Path $root "src\PyRuntimeInspector.App\PyRuntimeInspector.App.csproj") `
    -c $Configuration -r $RuntimeIdentifier --self-contained true --nologo -o $releaseDirectory
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File -Filter *.pdb | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $releaseDirectory
Copy-Item -LiteralPath (Join-Path $root "docs") -Destination $releaseDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $root "agent\pyproject.toml") -Destination (Join-Path $releaseDirectory "agent")

$verification = & (Join-Path $PSScriptRoot "Test-PortableRelease.ps1") `
    -ReleaseDirectory $releaseDirectory -ExpectedVersion $version -PythonExecutable $python

$archive = $null
if (-not $SkipArchive) {
    $archive = & (Join-Path $PSScriptRoot "New-PortableArchive.ps1") `
        -ReleaseDirectory $releaseDirectory -ArchivePath $archivePath
}

[pscustomobject]@{
    Version = $version
    RuntimeIdentifier = $RuntimeIdentifier
    ReleaseDirectory = $releaseDirectory
    ArchivePath = if ($archive) { $archive.ArchivePath } else { $null }
    Sha256 = if ($archive) { $archive.Sha256 } else { $null }
    FileCount = $verification.FileCount
}
