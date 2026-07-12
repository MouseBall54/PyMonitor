[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PreviousInstallerPath,
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [string]$ExpectedPreviousProductName = "PyRuntime Inspector",
    [string]$ExpectedProductName = "PyMonitor",
    [string]$LogDirectory
)

$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "MSI lifecycle verification requires Windows."
}
if (-not [Environment]::Is64BitProcess) {
    throw "MSI lifecycle verification requires 64-bit PowerShell."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "MSI lifecycle verification must run from an elevated PowerShell session."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$previousMsi = (Resolve-Path -LiteralPath $PreviousInstallerPath).Path
$currentMsi = (Resolve-Path -LiteralPath $InstallerPath).Path

function Assert-WorkspacePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $workspacePrefix = $root.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Lifecycle logs must stay inside the workspace: $fullPath"
    }
    return $fullPath
}

function Invoke-ComMethod($Target, [string]$Name, [object[]]$Arguments) {
    return $Target.GetType().InvokeMember(
        $Name,
        [Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Target,
        $Arguments)
}

function Get-MsiMetadata([string]$Path) {
    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = Invoke-ComMethod $installer "OpenDatabase" ([object[]]@($Path, 0))
        $values = [ordered]@{}
        foreach ($name in @("ProductName", "ProductVersion", "ProductCode", "UpgradeCode", "ALLUSERS")) {
            $view = $null
            $record = $null
            try {
                $query = 'SELECT `Value` FROM `Property` WHERE `Property`=''{0}''' -f $name
                $view = Invoke-ComMethod $database "OpenView" ([object[]]@($query))
                Invoke-ComMethod $view "Execute" ([object[]]@()) | Out-Null
                $record = Invoke-ComMethod $view "Fetch" ([object[]]@())
                $values[$name] = if ($null -eq $record) { $null } else {
                    $record.GetType().InvokeMember(
                        "StringData",
                        [Reflection.BindingFlags]::GetProperty,
                        $null,
                        $record,
                        [object[]]@(1))
                }
            }
            finally {
                if ($null -ne $view) {
                    Invoke-ComMethod $view "Close" ([object[]]@()) | Out-Null
                }
                foreach ($item in @($record, $view)) {
                    if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
                        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)
                    }
                }
            }
        }
        return [pscustomobject]$values
    }
    finally {
        foreach ($item in @($database, $installer)) {
            if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)
            }
        }
    }
}

function Test-ProductInstalled([string]$ProductCode) {
    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return $installer.ProductState($ProductCode) -eq 5
    }
    finally {
        if ($null -ne $installer -and [Runtime.InteropServices.Marshal]::IsComObject($installer)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }
}

function Get-RelatedProductCodes([string]$UpgradeCode) {
    $installer = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        return @($installer.RelatedProducts($UpgradeCode))
    }
    finally {
        if ($null -ne $installer -and [Runtime.InteropServices.Marshal]::IsComObject($installer)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        }
    }
}

function Invoke-MsiAction([string]$Action, [string]$Target, [string]$LogPath) {
    $arguments = @(
        "/$Action",
        "`"$Target`"",
        "/qn",
        "/norestart",
        "/L*v",
        "`"$LogPath`""
    )
    $process = Start-Process `
        -FilePath (Join-Path $env:SystemRoot "System32\msiexec.exe") `
        -ArgumentList $arguments `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($process.ExitCode -notin 0, 3010) {
        throw "msiexec /$Action failed with exit code $($process.ExitCode). See $LogPath"
    }
}

function Assert-PathExists([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description is missing: $Path"
    }
}

function Assert-PathRemoved([string]$Path, [string]$Description) {
    if (Test-Path -LiteralPath $Path) {
        throw "$Description remains after removal: $Path"
    }
}

function Assert-ShortcutTarget([string]$ShortcutPath, [string]$ExpectedTarget) {
    Assert-PathExists $ShortcutPath "Start Menu shortcut"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        if (-not $shortcut.TargetPath.Equals($ExpectedTarget, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Shortcut target '$($shortcut.TargetPath)' does not match '$ExpectedTarget'."
        }
    }
    finally {
        foreach ($item in @($shortcut, $shell)) {
            if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)
            }
        }
    }
}

$previous = Get-MsiMetadata $previousMsi
$current = Get-MsiMetadata $currentMsi
if ($previous.ProductName -ne $ExpectedPreviousProductName) {
    throw "Expected previous product '$ExpectedPreviousProductName', found '$($previous.ProductName)'."
}
if ($current.ProductName -ne $ExpectedProductName) {
    throw "Expected current product '$ExpectedProductName', found '$($current.ProductName)'."
}
if ($previous.UpgradeCode -ne $current.UpgradeCode) {
    throw "The previous and current installers do not share an UpgradeCode."
}
if ($previous.ProductCode -eq $current.ProductCode) {
    throw "The previous and current installers must use different ProductCodes."
}
if ([version]$current.ProductVersion -le [version]$previous.ProductVersion) {
    throw "Current MSI version must be newer than the previous MSI version."
}
if ($previous.ALLUSERS -ne "1" -or $current.ALLUSERS -ne "1") {
    throw "Both installers must be authored as per-machine packages."
}
$relatedProducts = @(Get-RelatedProductCodes $current.UpgradeCode)
if ($relatedProducts.Count -gt 0) {
    throw "A related product is already installed ($($relatedProducts -join ', ')). Remove it before running this isolated test."
}

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $LogDirectory = Join-Path $root "artifacts\installer-lifecycle\$stamp"
}
$logRoot = Assert-WorkspacePath $LogDirectory
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$programMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
$previousInstallDirectory = Join-Path $programFiles $ExpectedPreviousProductName
$currentInstallDirectory = Join-Path $programFiles $ExpectedProductName
$previousExecutable = Join-Path $previousInstallDirectory "PyRuntimeInspector.exe"
$currentExecutable = Join-Path $currentInstallDirectory "PyMonitor.exe"
$previousShortcutDirectory = Join-Path $programMenu $ExpectedPreviousProductName
$currentShortcutDirectory = Join-Path $programMenu $ExpectedProductName
$previousShortcut = Join-Path $previousShortcutDirectory "$ExpectedPreviousProductName.lnk"
$currentShortcut = Join-Path $currentShortcutDirectory "$ExpectedProductName.lnk"
$completed = $false
$failure = $null

try {
    Invoke-MsiAction "i" $previousMsi (Join-Path $logRoot "01-install-previous.log")
    if (-not (Test-ProductInstalled $previous.ProductCode)) {
        throw "The previous product was not registered after installation."
    }
    Assert-PathExists $previousExecutable "Previous executable"
    Assert-ShortcutTarget $previousShortcut $previousExecutable
    if ((Get-ItemProperty -LiteralPath "HKCU:\Software\PyRuntimeInspector" -Name Installed -ErrorAction Stop).Installed -ne 1) {
        throw "The previous product installation marker is invalid."
    }
    $relatedProducts = @(Get-RelatedProductCodes $current.UpgradeCode)
    if ($relatedProducts.Count -ne 1 -or $relatedProducts[0] -ne $previous.ProductCode) {
        throw "The previous product is not the sole related product after installation."
    }

    Invoke-MsiAction "i" $currentMsi (Join-Path $logRoot "02-upgrade-current.log")
    if (-not (Test-ProductInstalled $current.ProductCode)) {
        throw "The current product was not registered after upgrade."
    }
    if (Test-ProductInstalled $previous.ProductCode) {
        throw "The previous product remains registered after upgrade."
    }
    Assert-PathExists $currentExecutable "Current executable"
    Assert-ShortcutTarget $currentShortcut $currentExecutable
    Assert-PathRemoved $previousInstallDirectory "Previous installation directory"
    Assert-PathRemoved $previousShortcutDirectory "Previous Start Menu directory"
    if ($null -ne (Get-ItemProperty -LiteralPath "HKCU:\Software\PyRuntimeInspector" -Name Installed -ErrorAction SilentlyContinue)) {
        throw "The previous product installation marker remains after upgrade."
    }
    if ((Get-ItemProperty -LiteralPath "HKCU:\Software\PyMonitor" -Name Installed -ErrorAction Stop).Installed -ne 1) {
        throw "The PyMonitor installation marker is invalid."
    }
    $relatedProducts = @(Get-RelatedProductCodes $current.UpgradeCode)
    if ($relatedProducts.Count -ne 1 -or $relatedProducts[0] -ne $current.ProductCode) {
        throw "The current product is not the sole related product after upgrade."
    }

    Invoke-MsiAction "x" $current.ProductCode (Join-Path $logRoot "03-uninstall-current.log")
    if ((Test-ProductInstalled $previous.ProductCode) -or (Test-ProductInstalled $current.ProductCode)) {
        throw "A product remains registered after uninstall."
    }
    Assert-PathRemoved $previousInstallDirectory "Previous installation directory"
    Assert-PathRemoved $currentInstallDirectory "Current installation directory"
    Assert-PathRemoved $previousShortcutDirectory "Previous Start Menu directory"
    Assert-PathRemoved $currentShortcutDirectory "Current Start Menu directory"
    if ($null -ne (Get-ItemProperty -LiteralPath "HKCU:\Software\PyMonitor" -Name Installed -ErrorAction SilentlyContinue)) {
        throw "The PyMonitor installation marker remains after uninstall."
    }
    if ($null -ne (Get-ItemProperty -LiteralPath "HKCU:\Software\PyRuntimeInspector" -Name Installed -ErrorAction SilentlyContinue)) {
        throw "The previous product installation marker remains after uninstall."
    }
    if (@(Get-RelatedProductCodes $current.UpgradeCode).Count -ne 0) {
        throw "A related product remains registered after uninstall."
    }

    $completed = $true
}
catch {
    $failure = $_
}
finally {
    if (-not $completed) {
        foreach ($product in @($current, $previous)) {
            if (Test-ProductInstalled $product.ProductCode) {
                try {
                    Invoke-MsiAction "x" $product.ProductCode (Join-Path $logRoot "cleanup-$($product.ProductName -replace '[^A-Za-z0-9]+','-').log")
                }
                catch {
                    Write-Warning "Cleanup failed for $($product.ProductName): $($_.Exception.Message)"
                }
            }
        }
    }
}

if ($null -ne $failure) {
    throw $failure
}

[pscustomobject]@{
    PreviousProduct = "$($previous.ProductName) $($previous.ProductVersion)"
    CurrentProduct = "$($current.ProductName) $($current.ProductVersion)"
    UpgradeCode = $current.UpgradeCode
    PreviousInstalled = $true
    UpgradeRemovedPrevious = $true
    CurrentShortcutVerified = $true
    UninstallClean = $true
    LogDirectory = $logRoot
}
