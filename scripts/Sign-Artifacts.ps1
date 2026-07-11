[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [Parameter(Mandatory)]
    [string]$CertificatePath,
    [Parameter(Mandatory)]
    [Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
$files = foreach ($item in $Path) { (Resolve-Path -LiteralPath $item).Path }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
    -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -match "\\x64$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) { throw "Windows SDK signtool.exe was not found." }

$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword)
try {
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    foreach ($file in $files) {
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $certificate /p $plainPassword $file
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $file" }
        & $signtool.FullName verify /pa $file
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $file" }
    }
}
finally {
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    $plainPassword = $null
}

$files | ForEach-Object {
    $signature = Get-AuthenticodeSignature -LiteralPath $_
    [pscustomobject]@{
        Path = $_
        Status = $signature.Status
        Subject = $signature.SignerCertificate.Subject
        Thumbprint = $signature.SignerCertificate.Thumbprint
    }
}
