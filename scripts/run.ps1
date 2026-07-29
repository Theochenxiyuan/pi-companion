[CmdletBinding()]
param(
    [switch]$NoBuild,
    [string]$PiRuntimePath,
    [string]$NodeRuntimePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $env:LOCALAPPDATA 'PiCompanionTools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet).Source }
$desktopAssembly = Join-Path $repositoryRoot 'src\PiCompanion.Desktop\bin\Debug\net10.0-windows10.0.22000.0\PiCompanion.Desktop.dll'

if (!$NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
}

if (!(Test-Path -LiteralPath $desktopAssembly)) {
    throw 'Desktop build output is missing. Run scripts/build.ps1 first.'
}

if ([string]::IsNullOrWhiteSpace($env:windir)) {
    $env:windir = [Environment]::GetEnvironmentVariable('windir', 'Machine')
}

if (![string]::IsNullOrWhiteSpace($PiRuntimePath)) {
    $env:PI_COMPANION_PI_PATH = (Resolve-Path -LiteralPath $PiRuntimePath).Path
}

if (![string]::IsNullOrWhiteSpace($NodeRuntimePath)) {
    $env:PI_COMPANION_NODE_PATH = (Resolve-Path -LiteralPath $NodeRuntimePath).Path
}

& $dotnetCommand $desktopAssembly
