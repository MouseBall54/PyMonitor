[CmdletBinding()]
param(
    [string]$ReleaseDirectory,
    [string]$OutputDirectory,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { (Get-Command dotnet -ErrorAction Stop).Source }
[xml]$props = Get-Content -LiteralPath (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
if (-not $ReleaseDirectory) {
    $ReleaseDirectory = Join-Path $root "artifacts\PyRuntimeInspector-$version-win-x64"
}
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot "PyRuntimeInspector.exe"))) {
    throw "Build the portable release before building the installer."
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root "artifacts\installer"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$workspacePrefix = $root.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Installer output must stay inside the workspace: $outputRoot"
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

& $dotnet build (Join-Path $root "installer\PyRuntimeInspector.Installer\PyRuntimeInspector.Installer.wixproj") `
    -c $Configuration --nologo `
    -p:PublishDir="$releaseRoot" `
    -p:OutputPath="$outputRoot"
if ($LASTEXITCODE -ne 0) { throw "WiX installer build failed." }

$msi = Get-ChildItem -LiteralPath $outputRoot -Filter "PyRuntimeInspector-$version-win-x64.msi" -Recurse -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msi) { throw "WiX build completed without producing the expected MSI." }
$hash = (Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$($msi.FullName).sha256" `
    -Value "$hash  $($msi.Name)" -Encoding ascii

[pscustomobject]@{
    InstallerPath = $msi.FullName
    Sha256Path = "$($msi.FullName).sha256"
    Sha256 = $hash
    SizeBytes = $msi.Length
}
