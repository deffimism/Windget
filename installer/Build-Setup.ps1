param(
    [string]$MsiPath = (Join-Path $PSScriptRoot "..\release\Windget-v0.2.1-win-x64.msi"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\release\Windget-v0.2.1-win-x64-setup.exe"),
    [string]$Version = "0.2.1"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path -LiteralPath $MsiPath)) {
    throw "MSI file was not found: $MsiPath"
}

$projectPath = Join-Path $PSScriptRoot "WindgetSetup\WindgetSetup.csproj"
$sourcePath = Join-Path $PSScriptRoot "WindgetSetup\Program.cs"
$assemblyInfoPath = Join-Path $PSScriptRoot "WindgetSetup\AssemblyInfo.cs"
$iconPath = Join-Path $PSScriptRoot "..\WindgetApp\Assets\WindgetIcon.ico"
$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$outputDirectory = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath)
$resolvedOutputPath = Join-Path $outputDirectory.FullName (Split-Path -Leaf $OutputPath)

$cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path -LiteralPath $cscPath)) {
    $cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (!(Test-Path -LiteralPath $cscPath)) {
    throw "C# compiler was not found."
}

if (!(Test-Path -LiteralPath $iconPath)) {
    throw "Icon file was not found: $iconPath"
}

Remove-Item -LiteralPath $resolvedOutputPath -Force -ErrorAction SilentlyContinue

& $cscPath `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /codepage:65001 `
    "/win32icon:$iconPath" `
    "/resource:$resolvedMsiPath,WindgetInstaller.msi" `
    "/out:$resolvedOutputPath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $sourcePath `
    $assemblyInfoPath

if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $resolvedOutputPath)) {
    throw "Setup creation failed."
}

Write-Host "Created $resolvedOutputPath"
