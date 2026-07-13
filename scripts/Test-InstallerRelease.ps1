[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,
    [string]$ExpectedVersion = "26.7.13",
    [string]$ExpectedProductName = "PyMonitor",
    [string]$ExpectedManufacturer = "박영문",
    [string]$ExpectedUpgradeCode = "{2D73C23D-A566-4D8A-889C-F89FCE4A1377}",
    [string]$ExtractionDirectory,
    [string]$PythonExecutable = $(if ($env:PYTHON_EXECUTABLE) { $env:PYTHON_EXECUTABLE } else { "python" }),
    [switch]$KeepExtraction
)

$ErrorActionPreference = "Stop"
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "MSI verification requires Windows."
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$msi = (Resolve-Path -LiteralPath $InstallerPath).Path
if ([IO.Path]::GetExtension($msi) -ne ".msi") {
    throw "InstallerPath must point to an MSI file: $msi"
}

function Assert-WorkspacePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $workspacePrefix = $root.TrimEnd('\') + '\'
    if ($fullPath -eq $root -or -not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI verification output must stay inside the workspace: $fullPath"
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

function Get-MsiProperty($Database, [string]$Name) {
    $query = 'SELECT `Value` FROM `Property` WHERE `Property`=''{0}''' -f $Name
    $view = $null
    $record = $null
    try {
        $view = Invoke-ComMethod $Database "OpenView" ([object[]]@($query))
        Invoke-ComMethod $view "Execute" ([object[]]@()) | Out-Null
        $record = Invoke-ComMethod $view "Fetch" ([object[]]@())
        if ($null -eq $record) { return $null }
        return $record.GetType().InvokeMember(
            "StringData",
            [Reflection.BindingFlags]::GetProperty,
            $null,
            $record,
            [object[]]@(1))
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

function Get-MsiRow($Database, [string]$Query, [int]$ColumnCount) {
    $view = $null
    $record = $null
    try {
        $view = Invoke-ComMethod $Database "OpenView" ([object[]]@($Query))
        Invoke-ComMethod $view "Execute" ([object[]]@()) | Out-Null
        $record = Invoke-ComMethod $view "Fetch" ([object[]]@())
        if ($null -eq $record) { return $null }
        return ,@(
            foreach ($index in 1..$ColumnCount) {
                $record.GetType().InvokeMember(
                    "StringData",
                    [Reflection.BindingFlags]::GetProperty,
                    $null,
                    $record,
                    [object[]]@([int]$index))
            }
        )
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

$hashPath = "$msi.sha256"
if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) {
    throw "MSI SHA-256 sidecar is missing: $hashPath"
}
$sidecar = (Get-Content -Raw -LiteralPath $hashPath).Trim()
$sidecarMatch = [regex]::Match($sidecar, '^(?<hash>[0-9a-fA-F]{64})\s+(?<name>.+)$')
if (-not $sidecarMatch.Success) {
    throw "MSI SHA-256 sidecar has an invalid format: $hashPath"
}
$actualHash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sidecarMatch.Groups["hash"].Value.ToLowerInvariant() -ne $actualHash) {
    throw "MSI SHA-256 sidecar does not match the installer."
}
if ($sidecarMatch.Groups["name"].Value -ne [IO.Path]::GetFileName($msi)) {
    throw "MSI SHA-256 sidecar names a different file."
}

$windowsInstaller = $null
$database = $null
$summaryInformation = $null
try {
    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = Invoke-ComMethod $windowsInstaller "OpenDatabase" ([object[]]@([string]$msi, [int]0))
    $summaryInformation = $database.SummaryInformation(0)
    $metadata = [ordered]@{
        ProductName = Get-MsiProperty $database "ProductName"
        Manufacturer = Get-MsiProperty $database "Manufacturer"
        ProductVersion = Get-MsiProperty $database "ProductVersion"
        UpgradeCode = Get-MsiProperty $database "UpgradeCode"
        AllUsers = Get-MsiProperty $database "ALLUSERS"
        ArpProductIcon = Get-MsiProperty $database "ARPPRODUCTICON"
        SummaryCodepage = [string]$summaryInformation.Property(1)
        SummaryAuthor = [string]$summaryInformation.Property(4)
    }
    $shortcut = Get-MsiRow $database `
        'SELECT `Name`, `Target`, `Icon_`, `Directory_` FROM `Shortcut` WHERE `Shortcut`=''ApplicationStartMenuShortcut''' `
        4
    $registryMarker = Get-MsiRow $database `
        'SELECT `Registry`, `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry` WHERE `Key`=''Software\PyMonitor'' AND `Name`=''Installed''' `
        6
    $shortcutComponent = Get-MsiRow $database `
        'SELECT `Directory_`, `KeyPath` FROM `Component` WHERE `Component`=''StartMenuShortcut''' `
        2
    $programMenuParent = Get-MsiRow $database `
        'SELECT `Directory_Parent` FROM `Directory` WHERE `Directory`=''ProgramMenuDirectory''' `
        1
    $removeExistingProducts = Get-MsiRow $database `
        'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action`=''RemoveExistingProducts''' `
        1
}
finally {
    foreach ($item in @($summaryInformation, $database, $windowsInstaller)) {
        if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)
        }
    }
}

$expectedMetadata = [ordered]@{
    ProductName = $ExpectedProductName
    Manufacturer = $ExpectedManufacturer
    ProductVersion = $ExpectedVersion
    UpgradeCode = $ExpectedUpgradeCode
    AllUsers = "1"
    ArpProductIcon = "ProductIcon"
    SummaryCodepage = "949"
    SummaryAuthor = $ExpectedManufacturer
}
foreach ($name in $expectedMetadata.Keys) {
    if ($metadata[$name] -ne $expectedMetadata[$name]) {
        throw "Expected MSI $name '$($expectedMetadata[$name])', found '$($metadata[$name])'."
    }
}
if ($null -eq $shortcut -or $shortcut.Count -ne 4) {
    throw "MSI does not contain the expected Start Menu shortcut."
}
$shortcutLongName = ($shortcut[0] -split '\|')[-1]
if ($shortcutLongName -ne $ExpectedProductName) {
    throw "Expected shortcut name $ExpectedProductName, found $shortcutLongName."
}
if ($shortcut[1] -ne "[INSTALLFOLDER]$ExpectedProductName.exe") {
    throw "Expected shortcut target [INSTALLFOLDER]$ExpectedProductName.exe, found $($shortcut[1])."
}
if ($shortcut[2] -ne "ProductIcon" -or $shortcut[3] -ne "ProgramMenuDirectory") {
    throw "The Start Menu shortcut does not use the product icon and program-menu directory."
}
if ($null -eq $registryMarker -or $registryMarker.Count -ne 6) {
    throw "MSI does not contain the expected shortcut installation marker."
}
if ($registryMarker[1] -ne "1" -or
    $registryMarker[2] -ne "Software\PyMonitor" -or
    $registryMarker[3] -ne "Installed" -or
    $registryMarker[4] -ne "#1" -or
    $registryMarker[5] -ne "StartMenuShortcut") {
    throw "The installation marker must be an HKCU Software\PyMonitor value owned by the shortcut component."
}
if ($null -eq $shortcutComponent -or
    $shortcutComponent[0] -ne "ProgramMenuDirectory" -or
    $shortcutComponent[1] -ne $registryMarker[0]) {
    throw "The Start Menu component must use its HKCU installation marker as the KeyPath."
}
if ($null -eq $programMenuParent -or $programMenuParent[0] -ne "ProgramMenuFolder") {
    throw "The Start Menu shortcut must use the standard ProgramMenuFolder location."
}
if ($null -eq $removeExistingProducts -or $removeExistingProducts[0] -ne "1401") {
    throw "MSI does not contain the expected early major-upgrade removal action."
}

$callerProvidedExtraction = -not [string]::IsNullOrWhiteSpace($ExtractionDirectory)
if (-not $callerProvidedExtraction) {
    $baseName = [IO.Path]::GetFileNameWithoutExtension($msi)
    $ExtractionDirectory = Join-Path $root "artifacts\installer-verification\$baseName-$([Guid]::NewGuid().ToString('N'))"
}
$extractionRoot = Assert-WorkspacePath $ExtractionDirectory
$logPath = Assert-WorkspacePath "$extractionRoot.admin.log"
$verified = $false
try {
    if (Test-Path -LiteralPath $extractionRoot) {
        Remove-Item -LiteralPath $extractionRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
    }
    New-Item -ItemType Directory -Path $extractionRoot -Force | Out-Null

    $msiexec = Join-Path $env:SystemRoot "System32\msiexec.exe"
    $arguments = @(
        "/a",
        "`"$msi`"",
        "/qn",
        "/norestart",
        "TARGETDIR=`"$extractionRoot`"",
        "/L*v",
        "`"$logPath`""
    )
    $process = Start-Process -FilePath $msiexec -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin 0, 3010) {
        throw "MSI administrative extraction failed with exit code $($process.ExitCode). See $logPath"
    }

    $executables = @(Get-ChildItem -LiteralPath $extractionRoot -Recurse -File -Filter "$ExpectedProductName.exe")
    if ($executables.Count -ne 1) {
        throw "Expected exactly one extracted $ExpectedProductName.exe, found $($executables.Count)."
    }
    $legacyExecutables = @(Get-ChildItem -LiteralPath $extractionRoot -Recurse -File -Filter "PyRuntimeInspector.exe")
    if ($legacyExecutables.Count -ne 0) {
        throw "MSI still contains the legacy PyRuntimeInspector.exe."
    }

    $portableVerification = & (Join-Path $PSScriptRoot "Test-PortableRelease.ps1") `
        -ReleaseDirectory $executables[0].Directory.FullName `
        -ExpectedVersion $ExpectedVersion `
        -ExpectedProductName $ExpectedProductName `
        -ExpectedCompanyName $ExpectedManufacturer `
        -PythonExecutable $PythonExecutable

    $verified = $true
    [pscustomobject]@{
        InstallerPath = $msi
        ProductName = $metadata.ProductName
        Manufacturer = $metadata.Manufacturer
        ProductVersion = $metadata.ProductVersion
        UpgradeCode = $metadata.UpgradeCode
        Sha256 = $actualHash
        ExtractedReleaseDirectory = $executables[0].Directory.FullName
        ExtractedFileCount = $portableVerification.FileCount
    }
}
finally {
    if ($verified -and -not $KeepExtraction -and -not $callerProvidedExtraction) {
        if (Test-Path -LiteralPath $extractionRoot) {
            Remove-Item -LiteralPath $extractionRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $logPath) {
            Remove-Item -LiteralPath $logPath -Force
        }
    }
}
