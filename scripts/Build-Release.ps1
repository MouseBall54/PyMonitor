[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [string]$GitHubRepository
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$portable = & (Join-Path $PSScriptRoot "Build-PortableRelease.ps1") `
    -SkipArchive -SkipTests:$SkipTests -GitHubRepository $GitHubRepository
$exe = Join-Path $portable.ReleaseDirectory "PyMonitor.exe"

if ($CertificatePath) {
    if (-not $CertificatePassword) { throw "CertificatePassword is required when CertificatePath is set." }
    & (Join-Path $PSScriptRoot "Sign-Artifacts.ps1") `
        -Path $exe -CertificatePath $CertificatePath -CertificatePassword $CertificatePassword
}

$archivePath = Join-Path $root "artifacts\PyMonitor-$($portable.Version)-win-x64.zip"
$archive = & (Join-Path $PSScriptRoot "New-PortableArchive.ps1") `
    -ReleaseDirectory $portable.ReleaseDirectory -ArchivePath $archivePath
$installer = & (Join-Path $PSScriptRoot "Build-Installer.ps1") `
    -ReleaseDirectory $portable.ReleaseDirectory

if ($CertificatePath) {
    & (Join-Path $PSScriptRoot "Sign-Artifacts.ps1") `
        -Path $installer.InstallerPath -CertificatePath $CertificatePath -CertificatePassword $CertificatePassword
    $installerHash = (Get-FileHash -LiteralPath $installer.InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $installer.Sha256Path `
        -Value "$installerHash  $([IO.Path]::GetFileName($installer.InstallerPath))" -Encoding ascii
    $null = & (Join-Path $PSScriptRoot "Test-InstallerRelease.ps1") `
        -InstallerPath $installer.InstallerPath `
        -ExpectedVersion $portable.Version
}

[pscustomobject]@{
    Version = $portable.Version
    PortableDirectory = $portable.ReleaseDirectory
    PortableArchive = $archive.ArchivePath
    Installer = $installer.InstallerPath
    Signed = [bool]$CertificatePath
}
