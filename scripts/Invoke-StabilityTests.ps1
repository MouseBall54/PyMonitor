[CmdletBinding()]
param(
    [string]$PythonExecutable = $(if ($env:PYTHON_EXECUTABLE) { $env:PYTHON_EXECUTABLE } else { "python" }),
    [ValidateRange(1, 86400)]
    [int]$DurationSeconds = 60,
    [ValidateRange(1, 1000)]
    [int]$Cycles = 10,
    [ValidateRange(1, 4096)]
    [int]$MemoryGrowthLimitMB = 192
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$priorPythonPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = Join-Path $root "agent"
    & $PythonExecutable (Join-Path $root "tests\stability\stability_harness.py") `
        --python $PythonExecutable `
        --duration-seconds $DurationSeconds `
        --cycles $Cycles `
        --memory-growth-limit-mb $MemoryGrowthLimitMB
    if ($LASTEXITCODE -ne 0) {
        throw "Release stability tests failed."
    }
}
finally {
    $env:PYTHONPATH = $priorPythonPath
}
