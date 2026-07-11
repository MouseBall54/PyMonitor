[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [Parameter(Mandatory)]
    [string]$ArchivePath
)

$ErrorActionPreference = "Stop"
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$archiveFullPath = [IO.Path]::GetFullPath($ArchivePath)
$archiveParent = Split-Path -Parent $archiveFullPath
New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null

if (Test-Path -LiteralPath $archiveFullPath) {
    Remove-Item -LiteralPath $archiveFullPath
}
[IO.Compression.ZipFile]::CreateFromDirectory(
    $releaseRoot,
    $archiveFullPath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false
)
$hash = (Get-FileHash -LiteralPath $archiveFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$archiveFullPath.sha256"
Set-Content -LiteralPath $hashPath -Value "$hash  $([IO.Path]::GetFileName($archiveFullPath))" -Encoding ascii

[pscustomobject]@{
    ArchivePath = $archiveFullPath
    Sha256Path = $hashPath
    Sha256 = $hash
    SizeBytes = (Get-Item -LiteralPath $archiveFullPath).Length
}
