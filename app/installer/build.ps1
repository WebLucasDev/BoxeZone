<#
.SYNOPSIS
  Publishes Boxe Zone and builds its installer: app\installer\output\BoxeZone-Setup-<arch>.exe
.EXAMPLE
  .\app\installer\build.ps1 -Arch x64 -Version 1.2.0
#>
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$appDir = Split-Path $PSScriptRoot -Parent
$platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
# Assembly and file versions must be numeric: "1.2.0-rc.1" -> "1.2.0".
$numericVersion = ($Version -split '[-+]')[0]

if (-not (Test-Path (Join-Path $appDir 'appsettings.local.json'))) {
    throw 'app\appsettings.local.json not found: an installer without provider credentials could not sign in to anything.'
}

# Start clean: files left by an older local publish would end up in the installer.
$publishDir = Join-Path $appDir "bin\Release\net10.0-windows10.0.19041.0\win-$Arch\publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish (Join-Path $appDir 'BoxeZone.csproj') -c Release -p:Platform=$platform -p:Version=$numericVersion -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# $env:ISCC wins: the release workflow sets it to its pinned Inno Setup.
$iscc = @(
    $env:ISCC,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6.5.2+ (ISCC.exe) not found (the wizard images are PNG). Install it: winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" "/DArch=$Arch" (Join-Path $PSScriptRoot 'BoxeZone.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-Item (Join-Path $PSScriptRoot "output\BoxeZone-Setup-$Arch.exe")
