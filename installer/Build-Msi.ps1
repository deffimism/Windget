param(
    [string]$SourceDir = (Join-Path $PSScriptRoot "..\release\Windget-v0.2.0-win-x64"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\release\Windget-v0.2.0-win-x64.msi"),
    [string]$Version = "0.2.0"
)

$ErrorActionPreference = "Stop"

$productName = "Windget"
$manufacturer = "Windget"
$upgradeCode = "{8F7A1C40-7C67-4DF0-9DA7-37FBE36D2ED4}"

function New-DeterministicGuid([string]$value) {
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $bytes = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value))
        $guidBytes = New-Object byte[] 16
        [Array]::Copy($bytes, $guidBytes, 16)
        $guidBytes[7] = ($guidBytes[7] -band 0x0f) -bor 0x50
        $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80
        return "{" + ([Guid]::new($guidBytes).ToString().ToUpperInvariant()) + "}"
    }
    finally {
        $sha1.Dispose()
    }
}

function New-MsiIdentifier([string]$prefix, [string]$name) {
    $safe = [Regex]::Replace($name, "[^A-Za-z0-9_]", "_")
    if ($safe.Length -gt 50) {
        $safe = $safe.Substring(0, 50)
    }

    return "$prefix$safe"
}

$productCode = New-DeterministicGuid "$upgradeCode|$Version"

if (!(Test-Path -LiteralPath $SourceDir)) {
    throw "Source directory was not found: $SourceDir"
}

$sourceItem = Get-Item -LiteralPath $SourceDir
$sourcePath = $sourceItem.FullName
$outputItem = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath)
$resolvedOutputPath = Join-Path $outputItem.FullName (Split-Path -Leaf $OutputPath)
$cabPath = Join-Path $env:TEMP "Windget.cab"
$ddfPath = Join-Path $env:TEMP "Windget-$Version.ddf"
$manifestPath = Join-Path $env:TEMP "Windget-$Version.msi-manifest.tsv"

Remove-Item -LiteralPath $resolvedOutputPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $cabPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $ddfPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue

$files = Get-ChildItem -LiteralPath $sourcePath -File | Sort-Object Name
if ($files.Count -eq 0) {
    throw "No files were found in: $sourcePath"
}

$fileRows = @()
$sequence = 1
foreach ($file in $files) {
    $fileId = New-MsiIdentifier "fil_" $file.Name
    $componentId = New-MsiIdentifier "cmp_" $file.Name
    $fileRows += [PSCustomObject]@{
        File = $file
        FileId = $fileId
        ComponentId = $componentId
        Sequence = $sequence
    }
    $sequence++
}

$ddf = @(
    ".OPTION EXPLICIT",
    ".Set CabinetNameTemplate=Windget.cab",
    ".Set DiskDirectoryTemplate=$([IO.Path]::GetDirectoryName($cabPath))",
    ".Set CompressionType=MSZIP",
    ".Set Cabinet=on",
    ".Set Compress=on",
    ".Set MaxDiskSize=0"
)
foreach ($row in $fileRows) {
    $ddf += '"' + $row.File.FullName + '" "' + $row.FileId + '"'
}

Set-Content -LiteralPath $ddfPath -Value $ddf -Encoding ASCII
Push-Location $env:TEMP
try {
    & makecab.exe /F $ddfPath | Out-Null
}
finally {
    Pop-Location
}
if (!(Test-Path -LiteralPath $cabPath)) {
    throw "Cabinet creation failed: $cabPath"
}

$manifestLines = @()
foreach ($row in $fileRows) {
    $componentGuid = New-DeterministicGuid "$upgradeCode|$Version|$($row.File.Name)"
    $manifestLines += @($row.FileId, $row.ComponentId, $row.File.Name, $row.File.Length, $row.Sequence, $componentGuid) -join "`t"
}
Set-Content -LiteralPath $manifestPath -Value $manifestLines -Encoding ASCII

$createMsiScript = Join-Path $PSScriptRoot "Create-Msi.vbs"
& cscript.exe //nologo $createMsiScript $resolvedOutputPath $cabPath $manifestPath $Version $productCode $upgradeCode
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $resolvedOutputPath)) {
    throw "MSI creation failed: $resolvedOutputPath"
}

Remove-Item -LiteralPath $cabPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $ddfPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:TEMP "setup.inf") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:TEMP "setup.rpt") -Force -ErrorAction SilentlyContinue

Write-Host "Created $resolvedOutputPath"
