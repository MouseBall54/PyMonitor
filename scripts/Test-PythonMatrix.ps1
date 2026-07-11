[CmdletBinding()]
param(
    [string[]]$PythonVersions = @("3.10", "3.11", "3.12", "3.13", "3.14")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$uv = (Get-Command uv -ErrorAction Stop).Source
$priorPythonPath = $env:PYTHONPATH
$results = @()

try {
    $env:PYTHONPATH = Join-Path $root "agent"
    foreach ($version in $PythonVersions) {
        $environmentName = "PYTHON_$($version.Replace('.', '_'))_EXECUTABLE"
        $requestedPython = [Environment]::GetEnvironmentVariable($environmentName)
        if (-not $requestedPython) { $requestedPython = $version }

        $reportedVersion = & $uv run --python $requestedPython --with numpy -- `
            python -c "import platform; print(platform.python_version())"
        if ($LASTEXITCODE -ne 0) { throw "Could not provision CPython $version." }
        if (-not $reportedVersion.Trim().StartsWith("$version.", [StringComparison]::Ordinal)) {
            throw "Requested CPython $version, received $reportedVersion."
        }

        & $uv run --python $requestedPython --with numpy -- `
            python -m unittest discover -s (Join-Path $root "tests\agent_tests") -v
        if ($LASTEXITCODE -ne 0) { throw "Agent tests failed on CPython $reportedVersion." }

        $results += [pscustomobject]@{
            RequestedVersion = $version
            ActualVersion = $reportedVersion.Trim()
            Status = "Passed"
        }
    }
}
finally {
    $env:PYTHONPATH = $priorPythonPath
}

$results
